using Blockbuster.Core.Profiles;
using Blockbuster.Core.Security;
using Blockbuster.Infrastructure;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Persistence;
using Blockbuster.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blockbuster.Tests.Profiles;

public sealed class ProfileFoundationTests
{
    [Fact]
    public void PinHasherPreservesLeadingZeroesAndRejectsInvalidPins()
    {
        var hasher = new PinHasher();
        var hash = hasher.Hash("0123");
        Assert.True(hasher.Verify("0123", hash));
        Assert.False(hasher.Verify("1234", hash));
        Assert.False(hasher.Verify("123", hash));
        Assert.DoesNotContain("0123", hash, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => hasher.Hash("12a4"));
    }

    [Fact]
    public async Task ProfilesSupportCrudAndOptionalPinReplacement()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            await using var services = CreateServices(root, "0123");
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(token);
            var profiles = services.GetRequiredService<IProfileStore>();
            var hasher = services.GetRequiredService<IPinHasher>();
            var id = await profiles.CreateAsync(" Family ", hasher.Hash("0007"), token);
            var created = await profiles.GetAsync(id, token);
            Assert.Equal("Family", created!.Name);
            Assert.True(created.HasPin);
            Assert.True(hasher.Verify("0007", (await profiles.GetPinHashAsync(id, token))!));
            await profiles.UpdateAsync(id, "Kids", null, clearPin: true, token);
            Assert.False((await profiles.GetAsync(id, token))!.HasPin);
            await profiles.DeleteAsync(id, token);
            Assert.Empty(await profiles.ListAsync(token));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task BootstrapSecretIsUsedOnlyWhenCredentialDoesNotExist()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            await using var services = CreateServices(root, "0123");
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(token);
            var bootstrap = services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<AdministratorBootstrapService>().Single();
            await bootstrap.StartAsync(token);
            var credentials = services.GetRequiredService<IAdministratorCredentialStore>();
            var firstHash = await credentials.GetHashAsync(token);
            await bootstrap.StartAsync(token);
            Assert.Equal(firstHash, await credentials.GetHashAsync(token));
            Assert.True(services.GetRequiredService<IPinHasher>().Verify("0123", firstHash!));
        }
        finally { DeleteRoot(root); }
    }

    private static ServiceProvider CreateServices(string root, string bootstrapPin)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataRoot"] = root,
            ["MediaProbe:ExecutablePath"] = "ffprobe",
            ["Authentication:BootstrapPin"] = bootstrapPin
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBlockbusterInfrastructure(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "blockbuster-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
}
