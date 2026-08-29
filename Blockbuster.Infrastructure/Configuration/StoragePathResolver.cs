using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Configuration;

public interface IStoragePathResolver
{
    string DataRoot { get; }
    string DatabasePath { get; }
    string ArtworkPath { get; }
    string CachePath { get; }
    string GeneratedPath { get; }
    string LogsPath { get; }
    string BackupsPath { get; }
    string DataProtectionKeysPath { get; }
}

public sealed class StoragePathResolver(IOptions<StorageOptions> options) : IStoragePathResolver
{
    private readonly StorageOptions _options = options.Value;

    public string DataRoot => Path.GetFullPath(_options.DataRoot);
    public string DatabasePath => Resolve(_options.DatabasePath, "database", "blockbuster.db");
    public string ArtworkPath => Resolve(_options.ArtworkPath, "artwork");
    public string CachePath => Resolve(_options.CachePath, "cache");
    public string GeneratedPath => Resolve(_options.GeneratedPath, "generated");
    public string LogsPath => Resolve(_options.LogsPath, "logs");
    public string BackupsPath => Resolve(_options.BackupsPath, "backups");
    public string DataProtectionKeysPath => Resolve(_options.DataProtectionKeysPath, "data-protection-keys");

    private string Resolve(string? configuredPath, params string[] defaultParts)
    {
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.GetFullPath(Path.Combine([DataRoot, .. defaultParts]))
            : Path.GetFullPath(configuredPath);
    }
}
