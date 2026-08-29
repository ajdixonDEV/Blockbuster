using Blockbuster.Infrastructure;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Blockbuster.Tests.Configuration;

public sealed class ConfigurationTests
{
    [Fact]
    public void ConfigurationBindsAllSectionsAndPreservesLeadingZeroPin()
    {
        var dataRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var movieRoot = Path.Combine(dataRoot, "movies");
        var values = ValidConfiguration(dataRoot);
        values["Libraries:Sources:0:Id"] = "main";
        values["Libraries:Sources:0:MovieRoots:0"] = movieRoot;
        values["Authentication:BootstrapPin"] = "0123";

        using var provider = BuildProvider(values);

        var libraries = provider.GetRequiredService<IOptions<LibrariesOptions>>().Value;
        var authentication = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        Assert.Equal("main", Assert.Single(libraries.Sources).Id);
        Assert.Equal(movieRoot, Assert.Single(libraries.Sources[0].MovieRoots));
        Assert.Equal("0123", authentication.BootstrapPin);
    }

    [Fact]
    public void StoragePathsDefaultBeneathTheDataRoot()
    {
        var dataRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var provider = BuildProvider(ValidConfiguration(dataRoot));

        var paths = provider.GetRequiredService<IStoragePathResolver>();

        Assert.Equal(Path.Combine(dataRoot, "database", "blockbuster.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(dataRoot, "artwork"), paths.ArtworkPath);
        Assert.Equal(Path.Combine(dataRoot, "cache"), paths.CachePath);
        Assert.Equal(Path.Combine(dataRoot, "generated"), paths.GeneratedPath);
        Assert.Equal(Path.Combine(dataRoot, "logs"), paths.LogsPath);
        Assert.Equal(Path.Combine(dataRoot, "backups"), paths.BackupsPath);
        Assert.Equal(Path.Combine(dataRoot, "data-protection-keys"), paths.DataProtectionKeysPath);
    }

    [Fact]
    public void ExplicitAbsoluteStoragePathOverridesTheDefault()
    {
        var dataRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var externalLogs = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "logs"));
        var values = ValidConfiguration(dataRoot);
        values["Storage:LogsPath"] = externalLogs;
        using var provider = BuildProvider(values);

        Assert.Equal(externalLogs, provider.GetRequiredService<IStoragePathResolver>().LogsPath);
    }

    [Theory]
    [InlineData("Storage:DataRoot", "relative-data")]
    [InlineData("History:MaximumEventsPerProfile", "101")]
    [InlineData("Authentication:BootstrapPin", "123")]
    [InlineData("Authentication:BootstrapPin", "12a4")]
    [InlineData("Rooms:HardSeekThreshold", "00:00:00.500")]
    [InlineData("ReverseProxy:ForwardLimit", "0")]
    public void InvalidConfigurationIsRejected(string key, string value)
    {
        var dataRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var values = ValidConfiguration(dataRoot);
        values[key] = value;
        using var provider = BuildProvider(values);

        Assert.Throws<OptionsValidationException>(() => ResolveAllOptions(provider));
    }

    [Fact]
    public void DuplicateLibraryIdsAreRejectedCaseInsensitively()
    {
        var dataRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var values = ValidConfiguration(dataRoot);
        values["Libraries:Sources:0:Id"] = "Movies";
        values["Libraries:Sources:0:MovieRoots:0"] = Path.Combine(dataRoot, "movies-a");
        values["Libraries:Sources:1:Id"] = "movies";
        values["Libraries:Sources:1:MovieRoots:0"] = Path.Combine(dataRoot, "movies-b");
        using var provider = BuildProvider(values);

        Assert.Throws<OptionsValidationException>(() => ResolveAllOptions(provider));
    }

    [Fact]
    public void EnabledReverseProxyRequiresExplicitTrustedProxy()
    {
        var dataRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var values = ValidConfiguration(dataRoot);
        values["ReverseProxy:Enabled"] = "true";
        using var provider = BuildProvider(values);

        Assert.Throws<OptionsValidationException>(() => ResolveAllOptions(provider));
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddBlockbusterInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ValidConfiguration(string dataRoot) => new()
    {
        ["Storage:DataRoot"] = dataRoot,
        ["Scanning:Interval"] = "06:00:00",
        ["Scanning:Extensions:0"] = ".mp4",
        ["Scanning:Concurrency"] = "2",
        ["MediaProbe:ExecutablePath"] = "ffprobe",
        ["MediaProbe:Timeout"] = "00:00:30",
        ["Tmdb:Locale"] = "en-US",
        ["Tmdb:PosterSize"] = "w500",
        ["Tmdb:BackdropSize"] = "w1280",
        ["Playback:ProgressInterval"] = "00:00:10",
        ["Playback:ResumeThreshold"] = "00:00:30",
        ["History:MaximumEventsPerProfile"] = "100",
        ["Rooms:EmptyRoomExpiry"] = "00:05:00",
        ["Rooms:DriftCheckInterval"] = "00:00:05",
        ["Rooms:RateCorrectionThreshold"] = "00:00:00.750",
        ["Rooms:HardSeekThreshold"] = "00:00:03",
        ["Authentication:AdminCookieLifetime"] = "08:00:00"
    };

    private static void ResolveAllOptions(IServiceProvider provider)
    {
        _ = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<LibrariesOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<ScanningOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<MediaProbeOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<TmdbOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<PlaybackOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<HistoryOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<RoomsOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<ReverseProxyOptions>>().Value;
    }
}
