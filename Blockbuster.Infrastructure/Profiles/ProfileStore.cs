using Blockbuster.Core.Persistence;
using Blockbuster.Core.Profiles;
using Dapper;

namespace Blockbuster.Infrastructure.Profiles;

public sealed class ProfileStore(IDbConnectionFactory connections) : IProfileStore
{
    public async Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProfileRow>(new CommandDefinition(
            "SELECT id, name, pin_hash PinHash, created_at CreatedAt, updated_at UpdatedAt FROM profiles ORDER BY name COLLATE NOCASE",
            cancellationToken: cancellationToken));
        return rows.Select(ToProfile).ToList();
    }

    public async Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            "SELECT id, name, pin_hash PinHash, created_at CreatedAt, updated_at UpdatedAt FROM profiles WHERE id = @Id",
            new { Id = id.ToString("N") }, cancellationToken: cancellationToken));
        return row is null ? null : ToProfile(row);
    }

    public async Task<Guid> CreateAsync(string name, string? pinHash, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO profiles(id,name,pin_hash,created_at,updated_at) VALUES (@Id,@Name,@PinHash,@Now,@Now)",
            new { Id = id.ToString("N"), Name = NormalizeName(name), PinHash = pinHash, Now = now }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task UpdateAsync(Guid id, string name, string? replacementPinHash, bool clearPin, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE profiles SET name=@Name,
              pin_hash=CASE WHEN @ClearPin=1 THEN NULL WHEN @PinHash IS NOT NULL THEN @PinHash ELSE pin_hash END,
              updated_at=@Now WHERE id=@Id
            """, new { Id = id.ToString("N"), Name = NormalizeName(name), PinHash = replacementPinHash, ClearPin = clearPin ? 1 : 0, Now = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: cancellationToken));
        if (changed == 0) throw new KeyNotFoundException("Profile was not found.");
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM profiles WHERE id=@Id", new { Id = id.ToString("N") }, cancellationToken: cancellationToken));
    }

    public async Task<string?> GetPinHashAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT pin_hash FROM profiles WHERE id=@Id", new { Id = id.ToString("N") }, cancellationToken: cancellationToken));
    }

    private static string NormalizeName(string name)
    {
        var value = name.Trim();
        if (value.Length is < 1 or > 40) throw new ArgumentException("Profile name must be between 1 and 40 characters.", nameof(name));
        return value;
    }

    private static Profile ToProfile(ProfileRow row) => new(Guid.ParseExact(row.Id, "N"), row.Name, row.PinHash is not null,
        DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(row.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture));

    private sealed record ProfileRow(string Id, string Name, string? PinHash, string CreatedAt, string UpdatedAt);
}

public sealed class AdministratorCredentialStore(IDbConnectionFactory connections) : IAdministratorCredentialStore
{
    public async Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => await GetHashAsync(cancellationToken) is not null;

    public async Task<string?> GetHashAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT pin_hash FROM administrator_credential WHERE singleton_id=1", cancellationToken: cancellationToken));
    }

    public async Task SetHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO administrator_credential(singleton_id,pin_hash,updated_at) VALUES (1,@Hash,@Now)
            ON CONFLICT(singleton_id) DO UPDATE SET pin_hash=excluded.pin_hash, updated_at=excluded.updated_at
            """, new { Hash = hash, Now = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: cancellationToken));
    }
}
