using System.Data.Common;
using Blockbuster.Core.Persistence;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace Blockbuster.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory(IStoragePathResolver paths) : IDbConnectionFactory
{
    internal string ConnectionString => CreateConnectionString(paths.DatabasePath);

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);

        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string CreateConnectionString(string databasePath) => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
}
