namespace Blockbuster.Core.Scanning;

public enum ScanReason
{
    Startup,
    Scheduled,
    Manual
}

public sealed record LibraryRootScanResult(
    string LibrarySourceId,
    string RootPath,
    bool Succeeded,
    int DiscoveredFiles,
    int ChangedFiles,
    int MissingFiles,
    string? Error);

public sealed record LibraryScanResult(
    ScanReason Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<LibraryRootScanResult> Roots)
{
    public bool Succeeded => Roots.All(root => root.Succeeded);
}

public sealed record LibraryScannerStatus(
    bool IsRunning,
    ScanReason? Reason,
    DateTimeOffset? StartedAt,
    LibraryScanResult? LastResult);

public interface ILibraryScanner
{
    LibraryScannerStatus Status
    {
        get;
    }
    Task<LibraryScanResult> ScanAsync(ScanReason reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reconciles one configured root.  A root is the unit of availability: an
/// unsuccessful reconciliation must never make existing catalog files missing.
/// </summary>
public interface IConfiguredRootReconciler
{
    Task<LibraryRootScanResult> ReconcileAsync(string sourceId, string rootPath, CancellationToken cancellationToken = default);
    Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken = default);
}
