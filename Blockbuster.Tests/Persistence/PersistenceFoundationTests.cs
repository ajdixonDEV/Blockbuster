using Blockbuster.Core.Persistence;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Blockbuster.Tests.Persistence;

public sealed class PersistenceFoundationTests
{
    [Fact]
    public async Task MigrationIsIdempotentAndEnablesRequiredPragmas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = CreateTestRoot();
        try
        {
            await using var services = CreateServices(testRoot);
            var migrator = services.GetRequiredService<IDatabaseMigrator>();
            await migrator.MigrateAsync(cancellationToken);
            await migrator.MigrateAsync(cancellationToken);

            var connections = services.GetRequiredService<IDbConnectionFactory>();
            await using var connection = await connections.OpenConnectionAsync(cancellationToken);
            Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;", cancellationToken));
            Assert.Equal("1", await ScalarAsync(connection, "PRAGMA foreign_keys;", cancellationToken));
            Assert.Equal("5000", await ScalarAsync(connection, "PRAGMA busy_timeout;", cancellationToken));
            Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM SchemaVersions;", cancellationToken));
            Assert.Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='system_state';", cancellationToken));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task PooledConnectionsSupportConcurrentShortWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = CreateTestRoot();
        try
        {
            await using var services = CreateServices(testRoot);
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);
            var connections = services.GetRequiredService<IDbConnectionFactory>();

            var writes = Enumerable.Range(0, 12).Select(async index =>
            {
                await using var connection = await connections.OpenConnectionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO system_state(key,value,updated_at) VALUES ($key,$value,$now);";
                AddParameter(command, "$key", $"key-{index}");
                AddParameter(command, "$value", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddParameter(command, "$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            });
            await Task.WhenAll(writes);

            await using var verification = await connections.OpenConnectionAsync(cancellationToken);
            Assert.Equal("12", await ScalarAsync(verification, "SELECT COUNT(*) FROM system_state;", cancellationToken));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task DataProtectionKeysSurviveProviderRecreation()
    {
        var testRoot = CreateTestRoot();
        try
        {
            string protectedValue;
            await using (var firstServices = CreateServices(testRoot))
            {
                protectedValue = firstServices.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("persistence-test").Protect("movie-night");
            }
            await using (var secondServices = CreateServices(testRoot))
            {
                var value = secondServices.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("persistence-test").Unprotect(protectedValue);
                Assert.Equal("movie-night", value);
            }
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(testRoot, "data-protection-keys"), "*.xml"));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task ReadinessChecksReportHealthyWhenDependenciesAreConfigured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = CreateTestRoot();
        try
        {
            await using var services = CreateServices(testRoot);
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);
            var report = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync(
                registration => registration.Tags.Contains("ready"), cancellationToken);
            Assert.Equal(HealthStatus.Healthy, report.Status);
            Assert.Equal(5, report.Entries.Count);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task BackupCreatesConsistentTimestampedSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = CreateTestRoot();
        try
        {
            await using var services = CreateServices(testRoot);
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);
            var connections = services.GetRequiredService<IDbConnectionFactory>();
            await using (var connection = await connections.OpenConnectionAsync(cancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO system_state(key,value,updated_at) VALUES ('before','1','now');";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var backupPath = await services.GetRequiredService<IDatabaseBackupService>().CreateBackupAsync(cancellationToken: cancellationToken);

            await using var backup = new SqliteConnection($"Data Source={backupPath};Pooling=False");
            await backup.OpenAsync(cancellationToken);
            Assert.Equal("1", await ScalarAsync(backup, "SELECT COUNT(*) FROM system_state;", cancellationToken));
            Assert.StartsWith("blockbuster-", Path.GetFileName(backupPath), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static ServiceProvider CreateServices(string testRoot)
    {
        var ffprobe = Path.Combine(testRoot, "ffprobe-test");
        File.WriteAllText(ffprobe, string.Empty);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataRoot"] = testRoot,
            ["MediaProbe:ExecutablePath"] = ffprobe,
            ["Tmdb:Token"] = "test-token"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBlockbusterConfiguration(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "blockbuster-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestRoot(string testRoot)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(testRoot, recursive: true);
    }

    private static async Task<string?> ScalarAsync(System.Data.Common.DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
