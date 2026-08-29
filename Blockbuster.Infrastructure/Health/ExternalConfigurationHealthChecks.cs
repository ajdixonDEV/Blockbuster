using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Health;

public sealed class MediaProbeHealthCheck(IOptions<MediaProbeOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var executable = options.Value.ExecutablePath;
        var found = Path.IsPathRooted(executable) ? File.Exists(executable) : FindOnPath(executable);
        return Task.FromResult(found
            ? HealthCheckResult.Healthy("ffprobe is available.")
            : HealthCheckResult.Unhealthy($"ffprobe executable '{executable}' was not found."));
    }

    private static bool FindOnPath(string executable)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => extensions.Any(extension => File.Exists(Path.Combine(directory, executable + extension))));
    }
}

public sealed class LibraryRootsHealthCheck(IOptions<LibrariesOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var roots = options.Value.Sources.SelectMany(source => source.MovieRoots).ToArray();
        var missing = roots.Where(root => !Directory.Exists(root)).ToArray();
        return Task.FromResult(missing.Length == 0
            ? HealthCheckResult.Healthy(roots.Length == 0 ? "No media roots are configured." : "All configured media roots are available.")
            : HealthCheckResult.Unhealthy($"Unavailable media roots: {string.Join(", ", missing)}"));
    }
}

public sealed class TmdbConfigurationHealthCheck(IOptions<TmdbOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(options.Value.Token)
            ? HealthCheckResult.Degraded("TMDB token is not configured; metadata lookup will be unavailable.")
            : HealthCheckResult.Healthy("TMDB is configured."));
}
