using Blockbuster.Core.Scanning;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Scanning;

public sealed class LibraryScanner(
    IConfiguredRootReconciler reconciler,
    IOptions<LibrariesOptions> libraries) : ILibraryScanner, IDisposable
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _statusLock = new();
    private LibraryScannerStatus _status = new(false, null, null, null);
    private readonly LibrariesOptions _libraries = libraries.Value;

    public LibraryScannerStatus Status
    {
        get
        {
            lock (_statusLock)
                return _status;
        }
    }

    public async Task<LibraryScanResult> ScanAsync(ScanReason reason, CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken);
        var started = DateTimeOffset.UtcNow;
        lock (_statusLock)
            _status = new(true, reason, started, _status.LastResult);
        try
        {
            await reconciler.RecoverInterruptedRunsAsync(cancellationToken);
            var results = new List<LibraryRootScanResult>();
            foreach (var source in _libraries.Sources)
                foreach (var configuredRoot in source.MovieRoots)
                    results.Add(await reconciler.ReconcileAsync(source.Id, configuredRoot, cancellationToken));
            var result = new LibraryScanResult(reason, started, DateTimeOffset.UtcNow, results);
            lock (_statusLock)
                _status = new(false, null, null, result);
            return result;
        }
        finally
        {
            lock (_statusLock)
            {
                if (_status.IsRunning)
                    _status = _status with
                    {
                        IsRunning = false,
                        Reason = null,
                        StartedAt = null
                    };
            }
            _scanLock.Release();
        }
    }

    public void Dispose() => _scanLock.Dispose();
}
