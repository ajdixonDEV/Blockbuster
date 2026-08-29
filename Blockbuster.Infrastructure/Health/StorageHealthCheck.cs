using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blockbuster.Infrastructure.Health;

public sealed class StorageHealthCheck(IStoragePathResolver paths) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var directories = new[]
        {
            paths.DataRoot, Path.GetDirectoryName(paths.DatabasePath)!, paths.ArtworkPath, paths.CachePath,
            paths.GeneratedPath, paths.LogsPath, paths.BackupsPath, paths.DataProtectionKeysPath
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var directory in directories)
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, $".blockbuster-write-{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(probe, "ok", cancellationToken);
                File.Delete(probe);
            }
            return HealthCheckResult.Healthy("Configured storage directories are writable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy("A configured storage directory is not writable.", exception);
        }
    }
}
