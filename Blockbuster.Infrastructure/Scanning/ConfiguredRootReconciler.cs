using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Blockbuster.Core.Media;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Persistence;
using Blockbuster.Core.Scanning;
using Blockbuster.Infrastructure.Configuration;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Scanning;

/// <summary>
/// Owns a complete root observation. Observations are persisted separately from
/// catalog rows while the filesystem is traversed; a failed traversal therefore
/// cannot affect live availability.
/// </summary>
public sealed class ConfiguredRootReconciler(
    IMovieCatalogStore catalog,
    IDbConnectionFactory connections,
    IMediaProbe probe,
    IMovieMatchResolver resolver,
    IOptions<ScanningOptions> scanning,
    ILogger<ConfiguredRootReconciler> logger) : IConfiguredRootReconciler
{
    private readonly ScanningOptions _scanning = scanning.Value;
    private static readonly Action<ILogger, string, string, Exception?> ProbeFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(2101, nameof(ProbeFailed)), "Unable to probe movie file {LibrarySourceId}/{RelativePath}");
    private static readonly Action<ILogger, string, string, int, int, int, Exception?> RootCompleted =
        LoggerMessage.Define<string, string, int, int, int>(LogLevel.Information, new EventId(2102, nameof(RootCompleted)), "Movie root scan completed for {LibrarySourceId} at {RootPath}: {Discovered} files, {Changed} changed, {Missing} missing");
    private static readonly Action<ILogger, string, string, Exception?> RootFailed =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(2103, nameof(RootFailed)), "Movie root scan failed for {LibrarySourceId} at {RootPath}; availability was not reconciled");

    public async Task<LibraryRootScanResult> ReconcileAsync(string sourceId, string rootPath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Guid runId = Guid.Empty;
        var discovered = 0;
        var changed = 0;
        var promoted = false;
        try
        {
            runId = await catalog.StartScanRunAsync(sourceId, root, DateTimeOffset.UtcNow, cancellationToken);
            var files = EnumerateCompleteRoot(root);
            discovered = files.Count;
            var observations = new ConcurrentBag<Observation>();
            using var gate = new SemaphoreSlim(_scanning.Concurrency, _scanning.Concurrency);
            await Task.WhenAll(files.Select(async absolutePath =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var relative = Path.GetRelativePath(root, absolutePath);
                    var normalized = NormalizeRelativePath(relative);
                    var info = new FileInfo(absolutePath);
                    var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    var existing = await catalog.FindFileAsync(sourceId, root, normalized, cancellationToken);
                    var isChanged = existing is null || !existing.IsAvailable || existing.Length != info.Length || existing.LastModified != modified;
                    if (isChanged) Interlocked.Increment(ref changed);
                    MediaProbeResult? probeResult = null;
                    string? probeError = null;
                    if (isChanged)
                    {
                        try { probeResult = await probe.ProbeAsync(absolutePath, cancellationToken); }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            probeError = exception.Message;
                            ProbeFailed(logger, sourceId, relative, exception);
                        }
                    }
                    ScanMatchResolution? resolution = null;
                    if (isChanged && probeError is null && existing?.IsAssociated != true)
                        resolution = await resolver.PrepareAutomaticAsync(MovieFilenameParser.Parse(relative), cancellationToken);
                    observations.Add(new(relative, normalized, info.Length, modified, probeResult, probeError, existing?.Id ?? Guid.NewGuid(), existing?.IsAssociated == true, isChanged,
                        resolution is null ? null : JsonSerializer.Serialize(resolution)));
                }
                finally { gate.Release(); }
            }));

            await StageAsync(runId, observations, cancellationToken);
            // Promotion is one SQLite transaction: media facts, availability, scan
            // state, run completion, and staging cleanup become visible together.
            var promotion = await catalog.PromoteStagedRunAsync(runId, sourceId, root, discovered, changed, cancellationToken);
            promoted = true;
            RootCompleted(logger, sourceId, root, discovered, changed, promotion.MissingFiles, null);
            return new(sourceId, root, true, discovered, changed, promotion.MissingFiles, null);
        }
        catch (OperationCanceledException)
        {
            if (runId != Guid.Empty && !promoted)
            {
                await catalog.CompleteScanRunAsync(runId, false, discovered, changed, 0, "Scan cancelled before reconciliation completed.", CancellationToken.None);
                await DeleteObservationsAsync(runId, CancellationToken.None);
            }
            throw;
        }
        catch (Exception exception)
        {
            if (runId != Guid.Empty && !promoted)
            {
                await catalog.CompleteScanRunAsync(runId, false, discovered, changed, 0, exception.Message, CancellationToken.None);
                await DeleteObservationsAsync(runId, CancellationToken.None);
            }
            RootFailed(logger, sourceId, root, exception);
            return new(sourceId, root, promoted, discovered, changed, 0, exception.Message);
        }
    }

    public async Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE library_scan_runs SET completed_at=@Now,succeeded=0,error='Scan interrupted by application restart.'
            WHERE completed_at IS NULL
            """, new { Now = now }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM library_scan_observations WHERE run_id NOT IN (SELECT id FROM library_scan_runs WHERE completed_at IS NULL)", transaction: transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task StageAsync(Guid runId, IEnumerable<Observation> observations, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        foreach (var item in observations)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO library_scan_observations(run_id,normalized_relative_path,relative_path,length,last_modified_at,duration_seconds,container,video_codec,audio_codec,width,height,audio_channels,probe_error,assigned_media_file_id,match_resolution_json)
                VALUES(@RunId,@NormalizedPath,@RelativePath,@Length,@LastModified,@DurationSeconds,@Container,@VideoCodec,@AudioCodec,@Width,@Height,@AudioChannels,@ProbeError,@AssignedMediaFileId,@MatchResolutionJson)
                """, new { RunId = runId.ToString("N"), item.NormalizedPath, item.RelativePath, item.Length, LastModified = item.LastModified.ToString("O", CultureInfo.InvariantCulture), DurationSeconds = item.Probe?.Duration.TotalSeconds, item.Probe?.Container, item.Probe?.VideoCodec, item.Probe?.AudioCodec, item.Probe?.Width, item.Probe?.Height, item.Probe?.AudioChannels, item.ProbeError, AssignedMediaFileId = item.AssignedMediaFileId?.ToString("N"), item.MatchResolutionJson }, cancellationToken: cancellationToken));
    }

    private async Task DeleteObservationsAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM library_scan_observations WHERE run_id=@RunId", new { RunId = runId.ToString("N") }, cancellationToken: cancellationToken));
    }

    private List<string> EnumerateCompleteRoot(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Movie root '{root}' is unavailable.");
        var extensions = _scanning.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(path => extensions.Contains(Path.GetExtension(path))).ToList();
    }

    internal static string NormalizeRelativePath(string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath) || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
            throw new InvalidDataException("A scanned media path escaped its configured root.");
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private sealed record Observation(string RelativePath, string NormalizedPath, long Length, DateTimeOffset LastModified, MediaProbeResult? Probe, string? ProbeError, Guid? AssignedMediaFileId, bool WasAssociated, bool IsChanged, string? MatchResolutionJson);
}
