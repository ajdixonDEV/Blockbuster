using System.Collections.Concurrent;
using System.Text.Json;
using Blockbuster.Core.Media;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Scanning;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Movies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Scanning;

/// <summary>
/// Owns a complete root observation. Observations remain separate from catalog
/// rows until one atomic promotion, so failed traversal cannot affect live
/// availability.
/// </summary>
internal sealed class ConfiguredRootReconciler(
    MovieCatalogStore catalog,
    IMediaProbe probe,
    IAutomaticMovieMatchPreparer matches,
    IOptions<ScanningOptions> scanning,
    ILogger<ConfiguredRootReconciler> logger) : IConfiguredRootReconciler
{
    private static readonly Action<ILogger, string, string, Exception?>
        ProbeFailed = LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2101, nameof(ProbeFailed)),
            "Unable to probe movie file {LibrarySourceId}/{RelativePath}");

    private static readonly Action<
        ILogger,
        string,
        string,
        int,
        int,
        int,
        Exception?> RootCompleted =
            LoggerMessage.Define<string, string, int, int, int>(
                LogLevel.Information,
                new EventId(2102, nameof(RootCompleted)),
                "Movie root scan completed for {LibrarySourceId} at "
                + "{RootPath}: {Discovered} files, {Changed} changed, "
                + "{Missing} missing");

    private static readonly Action<ILogger, string, string, Exception?>
        RootFailed = LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2103, nameof(RootFailed)),
            "Movie root scan failed for {LibrarySourceId} at {RootPath}; "
            + "availability was not reconciled");

    private readonly ScanningOptions _scanning = scanning.Value;

    public async Task<LibraryRootScanResult> ReconcileAsync(
        string sourceId,
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var runId = Guid.Empty;
        var discovered = 0;
        var changed = 0;
        var promoted = false;

        try
        {
            runId = await catalog.StartScanRunAsync(
                sourceId,
                root,
                DateTimeOffset.UtcNow,
                cancellationToken);
            var files = EnumerateCompleteRoot(root);
            discovered = files.Count;
            var observations = new ConcurrentBag<StagedCatalogObservation>();
            using var gate = new SemaphoreSlim(
                _scanning.Concurrency,
                _scanning.Concurrency);

            await Task.WhenAll(files.Select(async absolutePath =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var observation = await ObserveAsync(
                        sourceId,
                        root,
                        absolutePath,
                        cancellationToken);
                    if (observation.IsChanged)
                    {
                        Interlocked.Increment(ref changed);
                    }

                    observations.Add(observation.Value);
                }
                finally
                {
                    gate.Release();
                }
            }));

            await catalog.StageObservationsAsync(
                runId,
                observations,
                cancellationToken);
            var missing = await catalog.PromoteStagedRunAsync(
                runId,
                sourceId,
                root,
                discovered,
                changed,
                cancellationToken);
            promoted = true;
            RootCompleted(
                logger,
                sourceId,
                root,
                discovered,
                changed,
                missing,
                null);
            return new LibraryRootScanResult(
                sourceId,
                root,
                true,
                discovered,
                changed,
                missing,
                null);
        }
        catch (OperationCanceledException)
        {
            if (runId != Guid.Empty && !promoted)
            {
                await catalog.FailScanRunAndClearStagingAsync(
                    runId,
                    discovered,
                    changed,
                    "Scan cancelled before reconciliation completed.",
                    CancellationToken.None);
            }

            throw;
        }
        catch (Exception exception)
        {
            if (runId != Guid.Empty && !promoted)
            {
                await catalog.FailScanRunAndClearStagingAsync(
                    runId,
                    discovered,
                    changed,
                    exception.Message,
                    CancellationToken.None);
            }

            RootFailed(logger, sourceId, root, exception);
            return new LibraryRootScanResult(
                sourceId,
                root,
                promoted,
                discovered,
                changed,
                0,
                exception.Message);
        }
    }

    public Task RecoverInterruptedRunsAsync(
        CancellationToken cancellationToken = default)
    {
        return catalog.RecoverInterruptedRunsAsync(cancellationToken);
    }

    private async Task<ObservedFile> ObserveAsync(
        string sourceId,
        string root,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(root, absolutePath);
        var normalized = NormalizeRelativePath(relative);
        var info = new FileInfo(absolutePath);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var existing = await catalog.FindFileAsync(
            sourceId,
            root,
            normalized,
            cancellationToken);
        var isChanged = existing is null
            || !existing.IsAvailable
            || existing.Length != info.Length
            || existing.LastModified != modified
            || !existing.HasUsableProbeFacts;

        MediaProbeResult? probeResult = null;
        string? probeError = null;
        if (isChanged)
        {
            try
            {
                probeResult = await probe.ProbeAsync(
                    absolutePath,
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                probeError = exception.Message;
                ProbeFailed(logger, sourceId, relative, exception);
            }
        }

        PreparedMovieMatch? resolution = null;
        if (isChanged && probeError is null && existing?.IsAssociated != true)
        {
            resolution = await matches.PrepareAsync(
                MovieFilenameParser.Parse(relative),
                cancellationToken);
        }

        var value = new StagedCatalogObservation(
            relative,
            normalized,
            info.Length,
            modified,
            probeResult,
            probeError,
            existing?.Id ?? Guid.NewGuid(),
            resolution is null ? null : JsonSerializer.Serialize(resolution));
        return new ObservedFile(value, isChanged);
    }

    private List<string> EnumerateCompleteRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Movie root '{root}' is unavailable.");
        }

        var extensions = _scanning.Extensions.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        return Directory
            .EnumerateFiles(
                root,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                })
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .ToList();
    }

    internal static string NormalizeRelativePath(string relativePath)
    {
        var escaped = Path.IsPathFullyQualified(relativePath)
            || relativePath
                .Split(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .Any(part => part == "..");
        if (escaped)
        {
            throw new InvalidDataException(
                "A scanned media path escaped its configured root.");
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private sealed record ObservedFile(
        StagedCatalogObservation Value,
        bool IsChanged);
}
