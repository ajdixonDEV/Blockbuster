using Blockbuster.Core.Scanning;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Scanning;

public sealed class LibraryScanHostedService(
    ILibraryScanner scanner,
    IConfiguredRootReconciler reconciler,
    IOptions<ScanningOptions> options,
    ILogger<LibraryScanHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, ScanReason, Exception?> ScanFailed =
        LoggerMessage.Define<ScanReason>(LogLevel.Error, new EventId(2110, nameof(ScanFailed)), "{ScanReason} library scan failed");
    private readonly ScanningOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recovery is deliberately independent of ScanOnStartup: a restarted
        // service must not leave a run looking active until its next schedule.
        await reconciler.RecoverInterruptedRunsAsync(stoppingToken);
        if (_options.ScanOnStartup)
            await RunSafelyAsync(ScanReason.Startup, stoppingToken);

        using var timer = new PeriodicTimer(_options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunSafelyAsync(ScanReason.Scheduled, stoppingToken);
    }

    private async Task RunSafelyAsync(ScanReason reason, CancellationToken cancellationToken)
    {
        try { await scanner.ScanAsync(reason, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { ScanFailed(logger, reason, exception); }
    }
}
