using System.Security.Cryptography;
using Blockbuster.Core.Profiles;
using Blockbuster.Core.Security;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Operations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Security;

public sealed class PinHasher : IPinHasher
{
    private const int Iterations = 210_000;
    public string Hash(string pin)
    {
        Validate(pin);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string pin, string encodedHash)
    {
        if (!IsValid(pin)) return false;
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    public static bool IsValid(string? pin) => pin is { Length: 4 } && pin.All(char.IsAsciiDigit);
    public static void Validate(string pin)
    {
        if (!IsValid(pin)) throw new ArgumentException("PIN must contain exactly four digits.", nameof(pin));
    }
}

public sealed class AdministratorPinService(IAdministratorCredentialStore credentials, IPinHasher hasher) : IAdministratorPinResetService
{
    public async Task ResetAsync(string pin, CancellationToken cancellationToken = default) =>
        await credentials.SetHashAsync(hasher.Hash(pin), cancellationToken);
}

public sealed class AdministratorBootstrapService(
    IAdministratorCredentialStore credentials,
    IPinHasher hasher,
    IOptions<AuthenticationOptions> options,
    ILogger<AdministratorBootstrapService> logger) : IHostedService
{
    private static readonly Action<ILogger, Exception?> BootstrapComplete =
        LoggerMessage.Define(LogLevel.Information, new EventId(1201, nameof(BootstrapComplete)),
            "Initialized the administrator credential from the bootstrap secret; future starts will ignore that secret");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await credentials.ExistsAsync(cancellationToken)) return;
        var pin = options.Value.BootstrapPin;
        if (string.IsNullOrEmpty(pin))
            throw new InvalidOperationException("No administrator credential exists. Supply Authentication:BootstrapPin for first startup.");
        await credentials.SetHashAsync(hasher.Hash(pin), cancellationToken);
        BootstrapComplete(logger, null);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
