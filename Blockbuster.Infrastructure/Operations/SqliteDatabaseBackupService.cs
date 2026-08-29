using Blockbuster.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace Blockbuster.Infrastructure.Persistence;

public interface IDatabaseBackupService
{
    Task<string> CreateBackupAsync(string? outputPath = null, CancellationToken cancellationToken = default);
}

public sealed class SqliteDatabaseBackupService(
    IStoragePathResolver paths,
    SqliteConnectionFactory connections) : IDatabaseBackupService
{
    public async Task<string> CreateBackupAsync(string? outputPath = null, CancellationToken cancellationToken = default)
    {
        var destinationPath = outputPath ?? Path.Combine(
            paths.BackupsPath,
            $"blockbuster-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.db");
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("Backup output path must be absolute.", nameof(outputPath));

        destinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
            throw new IOException($"Backup destination already exists: {destinationPath}");

        await using var source = new SqliteConnection(connections.ConnectionString);
        await source.OpenAsync(cancellationToken);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        return destinationPath;
    }
}
