using Blockbuster.Core.Media;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Scanning;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Scanning;

public sealed class LibraryScanner(
    IMovieCatalogStore catalog,
    IMediaProbe probe,
    IMovieMetadataProvider metadata,
    IArtworkCache artwork,
    IOptions<LibrariesOptions> libraries,
    IOptions<ScanningOptions> scanning,
    ILogger<LibraryScanner> logger) : ILibraryScanner, IDisposable
{
    private static readonly Action<ILogger, string, string, Exception?> ProbeFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(2101, nameof(ProbeFailed)), "Unable to probe movie file {LibrarySourceId}/{RelativePath}");
    private static readonly Action<ILogger, string, string, int, int, int, Exception?> RootCompleted =
        LoggerMessage.Define<string, string, int, int, int>(LogLevel.Information, new EventId(2102, nameof(RootCompleted)), "Movie root scan completed for {LibrarySourceId} at {RootPath}: {Discovered} files, {Changed} changed, {Missing} missing");
    private static readonly Action<ILogger, string, string, Exception?> RootFailed =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(2103, nameof(RootFailed)), "Movie root scan failed for {LibrarySourceId} at {RootPath}; availability was not reconciled");
    private static readonly Action<ILogger, string, int, Exception?> SearchFailed =
        LoggerMessage.Define<string, int>(LogLevel.Warning, new EventId(2104, nameof(SearchFailed)), "TMDB search failed for {Title} ({Year})");
    private static readonly Action<ILogger, int, Exception?> ArtworkFailed =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(2105, nameof(ArtworkFailed)), "Artwork caching failed for TMDB movie {TmdbId}; metadata will remain usable");
    private static readonly Action<ILogger, string, int, Exception?> DetailsFailed =
        LoggerMessage.Define<string, int>(LogLevel.Warning, new EventId(2106, nameof(DetailsFailed)), "TMDB details failed for {Title} ({Year})");
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _statusLock = new();
    private LibraryScannerStatus _status = new(false, null, null, null);
    private readonly LibrariesOptions _libraries = libraries.Value;
    private readonly ScanningOptions _scanning = scanning.Value;

    public LibraryScannerStatus Status { get { lock (_statusLock) return _status; } }

    public async Task<LibraryScanResult> ScanAsync(ScanReason reason, CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken);
        var started = DateTimeOffset.UtcNow;
        lock (_statusLock) _status = new(true, reason, started, _status.LastResult);
        try
        {
            var results = new List<LibraryRootScanResult>();
            foreach (var source in _libraries.Sources)
            foreach (var configuredRoot in source.MovieRoots)
                results.Add(await ScanRootAsync(source.Id, configuredRoot, cancellationToken));
            var result = new LibraryScanResult(reason, started, DateTimeOffset.UtcNow, results);
            lock (_statusLock) _status = new(false, null, null, result);
            return result;
        }
        finally
        {
            lock (_statusLock)
            {
                if (_status.IsRunning) _status = _status with { IsRunning = false, Reason = null, StartedAt = null };
            }
            _scanLock.Release();
        }
    }

    private async Task<LibraryRootScanResult> ScanRootAsync(string sourceId, string configuredRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var runId = await catalog.StartScanRunAsync(sourceId, root, DateTimeOffset.UtcNow, cancellationToken);
        var discovered = 0;
        var changed = 0;
        try
        {
            var files = EnumerateCompleteRoot(root);
            discovered = files.Count;
            var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
            using var concurrency = new SemaphoreSlim(_scanning.Concurrency, _scanning.Concurrency);
            var tasks = files.Select(async absolutePath =>
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    var relative = Path.GetRelativePath(root, absolutePath);
                    var normalized = NormalizeRelativePath(relative);
                    seen.Add(normalized);
                    var info = new FileInfo(absolutePath);
                    var lastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    var existing = await catalog.FindFileAsync(sourceId, root, normalized, cancellationToken);
                    if (existing is not null && existing.IsAvailable && existing.Length == info.Length && existing.LastModified == lastModified)
                        return;

                    Interlocked.Increment(ref changed);
                    MediaProbeResult? probeResult = null;
                    string? probeError = null;
                    try { probeResult = await probe.ProbeAsync(absolutePath, cancellationToken); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        probeError = exception.Message;
                        ProbeFailed(logger, sourceId, relative, exception);
                    }

                    var stored = await catalog.UpsertFileAsync(new MediaFileUpsert(
                        sourceId, root, relative, normalized, info.Length, lastModified, probeResult, probeError), cancellationToken);
                    var parsed = MovieFilenameParser.Parse(relative);
                    if (probeError is not null)
                    {
                        await catalog.QueuePendingMatchAsync(stored.Id, parsed,
                            new MovieMatchDecision(MovieMatchOutcome.ProbeFailed, null, [], "ffprobe could not read this file; inspect the probe error and retry."), cancellationToken);
                        return;
                    }
                    if (existing?.IsAssociated == true) return;
                    await MatchAsync(stored.Id, parsed, cancellationToken);
                }
                finally { concurrency.Release(); }
            });
            await Task.WhenAll(tasks);
            var missing = await catalog.MarkMissingAsync(sourceId, root, seen.ToArray(), cancellationToken);
            await catalog.CompleteScanRunAsync(runId, true, discovered, changed, missing, null, cancellationToken);
            RootCompleted(logger, sourceId, root, discovered, changed, missing, null);
            return new(sourceId, root, true, discovered, changed, missing, null);
        }
        catch (OperationCanceledException)
        {
            await catalog.CompleteScanRunAsync(runId, false, discovered, changed, 0, "Scan cancelled before reconciliation completed.", CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var error = exception.Message;
            await catalog.CompleteScanRunAsync(runId, false, discovered, changed, 0, error, CancellationToken.None);
            RootFailed(logger, sourceId, root, exception);
            return new(sourceId, root, false, discovered, changed, 0, error);
        }
    }

    private async Task MatchAsync(Guid mediaFileId, ParsedMovieFileName parsed, CancellationToken cancellationToken)
    {
        if (parsed.Year is null || !metadata.IsConfigured)
        {
            await catalog.QueuePendingMatchAsync(mediaFileId, parsed, MovieMatcher.Decide(parsed, [], metadata.IsConfigured), cancellationToken);
            return;
        }

        IReadOnlyList<MovieMetadataCandidate> candidates;
        try { candidates = await metadata.SearchAsync(parsed.Title, parsed.Year.Value, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SearchFailed(logger, parsed.Title, parsed.Year.Value, exception);
            await catalog.QueuePendingMatchAsync(mediaFileId, parsed,
                new MovieMatchDecision(MovieMatchOutcome.ProviderUnavailable, null, [], "TMDB could not be reached; retry matching later."), cancellationToken);
            return;
        }

        var decision = MovieMatcher.Decide(parsed, candidates, metadata.IsConfigured);
        if (decision.Accepted is null)
        {
            await catalog.QueuePendingMatchAsync(mediaFileId, parsed, decision, cancellationToken);
            return;
        }

        try
        {
            var details = await metadata.GetAsync(decision.Accepted.TmdbId, cancellationToken);
            if (details is null)
            {
                await catalog.QueuePendingMatchAsync(mediaFileId, parsed,
                    decision with { Outcome = MovieMatchOutcome.ProviderUnavailable, Accepted = null, Explanation = "TMDB details were unavailable; retry matching later." }, cancellationToken);
                return;
            }
            string? poster = null;
            string? backdrop = null;
            try
            {
                poster = await artwork.CacheAsync("poster", details.TmdbId, details.PosterPath, cancellationToken);
                backdrop = await artwork.CacheAsync("backdrop", details.TmdbId, details.BackdropPath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ArtworkFailed(logger, details.TmdbId, exception);
            }
            await catalog.ApplyMetadataAsync(mediaFileId, details, poster, backdrop, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DetailsFailed(logger, parsed.Title, parsed.Year!.Value, exception);
            await catalog.QueuePendingMatchAsync(mediaFileId, parsed,
                decision with { Outcome = MovieMatchOutcome.ProviderUnavailable, Accepted = null, Explanation = "TMDB details could not be loaded; retry matching later." }, cancellationToken);
        }
    }

    private List<string> EnumerateCompleteRoot(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Movie root '{root}' is unavailable.");
        var extensions = _scanning.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root, "*", options).Where(path => extensions.Contains(Path.GetExtension(path))).ToList();
    }

    internal static string NormalizeRelativePath(string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath) || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
            throw new InvalidDataException("A scanned media path escaped its configured root.");
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    public void Dispose() => _scanLock.Dispose();
}
