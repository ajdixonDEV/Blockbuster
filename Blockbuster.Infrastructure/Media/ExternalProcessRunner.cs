using System.Diagnostics;

namespace Blockbuster.Infrastructure.Media;

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IExternalProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The process timeout must be positive.");
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"The process '{startInfo.FileName}' did not start.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(
            CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(
            CancellationToken.None);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(waitSource.Token);
        }
        catch (OperationCanceledException)
        {
            TerminateProcessTree(process);
            await AwaitTerminationAsync(process, outputTask, errorTask);

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"The process '{startInfo.FileName}' exceeded the configured "
                + $"timeout of {timeout}.");
        }

        await Task.WhenAll(outputTask, errorTask);
        return new ProcessExecutionResult(
            process.ExitCode,
            outputTask.Result,
            errorTask.Result);
    }

    private static async Task AwaitTerminationAsync(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The process exited between cancellation and the termination check.
        }

        await Task.WhenAll(outputTask, errorTask);
    }

    private static void TerminateProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited before the kill request reached it.
        }
    }
}
