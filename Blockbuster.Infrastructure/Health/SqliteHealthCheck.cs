using Blockbuster.Core.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blockbuster.Infrastructure.Health;

public sealed class SqliteHealthCheck(IDbConnectionFactory connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await connections.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)
                ? HealthCheckResult.Healthy("SQLite is reachable and passed quick_check.")
                : HealthCheckResult.Unhealthy($"SQLite quick_check returned '{result}'.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("SQLite is unavailable.", exception);
        }
    }
}
