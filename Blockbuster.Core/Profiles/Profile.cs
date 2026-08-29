namespace Blockbuster.Core.Profiles;

public sealed record Profile(Guid Id, string Name, bool HasPin, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public interface IProfileStore
{
    Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default);
    Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(string name, string? pinHash, CancellationToken cancellationToken = default);
    Task UpdateAsync(Guid id, string name, string? replacementPinHash, bool clearPin, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<string?> GetPinHashAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IAdministratorCredentialStore
{
    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
    Task<string?> GetHashAsync(CancellationToken cancellationToken = default);
    Task SetHashAsync(string hash, CancellationToken cancellationToken = default);
}
