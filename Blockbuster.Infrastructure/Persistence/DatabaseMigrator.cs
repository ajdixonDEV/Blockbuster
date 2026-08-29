using System.Reflection;
using Blockbuster.Core.Persistence;
using Blockbuster.Infrastructure.Configuration;
using DbUp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blockbuster.Infrastructure.Persistence;

public interface IDatabaseMigrator
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
}

public sealed class DatabaseMigrator(
    IStoragePathResolver paths,
    IDbConnectionFactory connections,
    SqliteConnectionFactory sqliteConnections,
    ILogger<DatabaseMigrator> logger) : IHostedService, IDatabaseMigrator
{
    private static readonly Action<ILogger, string, int, Exception?> MigrationComplete =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(1001, nameof(MigrationComplete)),
            "SQLite migrations are current at {DatabasePath}; {ScriptCount} script(s) executed");

    public async Task StartAsync(CancellationToken cancellationToken) => await MigrateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);

        await using (var connection = await connections.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SQLite did not enable WAL mode; reported '{mode}'.");
        }

        var upgrader = DeployChanges.To
            .SqliteDatabase(sqliteConnections.ConnectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), name => name.Contains(".Persistence.Migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(logger)
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException("Database migration failed.", result.Error);

        var scriptCount = result.Scripts.Count();
        MigrationComplete(logger, paths.DatabasePath, scriptCount, null);
    }
}
