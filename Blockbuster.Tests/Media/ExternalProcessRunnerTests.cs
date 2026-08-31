using System.Diagnostics;
using Blockbuster.Infrastructure.Media;
using Xunit;

namespace Blockbuster.Tests.Media;

public sealed class ExternalProcessRunnerTests
{
    [Fact]
    public async Task TimeoutTerminatesTheRunningProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pidPath = CreatePidPath();
        try
        {
            var runner = new ExternalProcessRunner();
            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                runner.RunAsync(
                    CreateLongRunningProcess(pidPath),
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken));

            Assert.Contains("exceeded", exception.Message);
            await AssertProcessExitedAsync(pidPath, cancellationToken);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task CallerCancellationTerminatesTheRunningProcess()
    {
        var pidPath = CreatePidPath();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(500));
        try
        {
            var runner = new ExternalProcessRunner();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunAsync(
                    CreateLongRunningProcess(pidPath),
                    TimeSpan.FromSeconds(30),
                    cancellation.Token));

            await AssertProcessExitedAsync(pidPath, CancellationToken.None);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    private static ProcessStartInfo CreateLongRunningProcess(string pidPath)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell";
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                $"$PID | Set-Content -LiteralPath '{EscapePowerShell(pidPath)}'; "
                + "Start-Sleep -Seconds 30");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                $"echo $$ > '{pidPath.Replace("'", "'\\''", StringComparison.Ordinal)}'; "
                + "sleep 30");
        }

        return startInfo;
    }

    private static async Task AssertProcessExitedAsync(
        string pidPath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!File.Exists(pidPath) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, cancellationToken);
        }

        Assert.True(File.Exists(pidPath), "The child process did not publish its PID.");
        var text = await File.ReadAllTextAsync(pidPath, cancellationToken);
        var pid = int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);

        await Task.Delay(100, cancellationToken);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    private static string CreatePidPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"blockbuster-process-{Guid.NewGuid():N}.pid");

    private static string EscapePowerShell(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
