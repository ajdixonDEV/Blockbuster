using Blockbuster.Core.Media;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Persistence;
using Blockbuster.Core.Playback;
using Blockbuster.Core.Profiles;
using Blockbuster.Core.Scanning;
using Blockbuster.Core.Security;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Health;
using Blockbuster.Infrastructure.Media;
using Blockbuster.Infrastructure.Movies;
using Blockbuster.Infrastructure.Operations;
using Blockbuster.Infrastructure.Persistence;
using Blockbuster.Infrastructure.Profiles;
using Blockbuster.Infrastructure.Scanning;
using Blockbuster.Infrastructure.Security;
using Blockbuster.Core.SharedPlayback;
using Blockbuster.Infrastructure.SharedPlayback;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBlockbusterInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddValidated<StorageOptions, StorageOptionsValidator>(services, configuration, StorageOptions.SectionName);
        AddValidated<LibrariesOptions, LibrariesOptionsValidator>(services, configuration, LibrariesOptions.SectionName);
        AddValidated<ScanningOptions, ScanningOptionsValidator>(services, configuration, ScanningOptions.SectionName);
        AddValidated<MediaProbeOptions, MediaProbeOptionsValidator>(services, configuration, MediaProbeOptions.SectionName);
        AddValidated<TmdbOptions, TmdbOptionsValidator>(services, configuration, TmdbOptions.SectionName);
        AddValidated<PlaybackOptions, PlaybackOptionsValidator>(services, configuration, PlaybackOptions.SectionName);
        AddValidated<HistoryOptions, HistoryOptionsValidator>(services, configuration, HistoryOptions.SectionName);
        AddValidated<RoomsOptions, RoomsOptionsValidator>(services, configuration, RoomsOptions.SectionName);
        AddValidated<AuthenticationOptions, AuthenticationOptionsValidator>(services, configuration, AuthenticationOptions.SectionName);
        AddValidated<ReverseProxyOptions, ReverseProxyOptionsValidator>(services, configuration, ReverseProxyOptions.SectionName);

        services.AddSingleton<IStoragePathResolver, StoragePathResolver>();
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<IDbConnectionFactory>(provider => provider.GetRequiredService<SqliteConnectionFactory>());
        services.AddSingleton<IDatabaseBackupService, SqliteDatabaseBackupService>();
        services.AddSingleton<ISecretPinReader, ConsoleSecretPinReader>();
        services.AddSingleton<OperatorCommandDispatcher>();
        services.AddSingleton<IProfileStore, ProfileStore>();
        services.AddSingleton<IAdministratorCredentialStore, AdministratorCredentialStore>();
        services.AddSingleton<IPinHasher, PinHasher>();
        services.AddSingleton<IAdministratorPinResetService, AdministratorPinService>();
        services.AddSingleton<IMovieCatalogStore, MovieCatalogStore>();
        services.AddSingleton<MovieLibrary>();
        services.AddSingleton<IMovieLibrary>(provider => provider.GetRequiredService<MovieLibrary>());
        services.AddSingleton<IPlaybackProgressStore>(provider => provider.GetRequiredService<MovieLibrary>());
        services.AddSingleton<ISharedPlaybackCoordinator, InMemorySharedPlaybackCoordinator>();
        services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
        services.AddHttpClient<IMovieMetadataProvider, TmdbMovieMetadataProvider>(client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<IArtworkCache, ArtworkCache>(client => client.Timeout = TimeSpan.FromMinutes(2));
        services.AddSingleton<ILibraryScanner, LibraryScanner>();
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<IDatabaseMigrator>(provider => provider.GetRequiredService<DatabaseMigrator>());
        services.AddHostedService(provider => provider.GetRequiredService<DatabaseMigrator>());
        services.AddHostedService<AdministratorBootstrapService>();
        services.AddHostedService<LibraryScanHostedService>();

        services.AddDataProtection().SetApplicationName("Blockbuster");
        services.ConfigureOptions<DataProtectionOptionsSetup>();

        services.AddHealthChecks()
            .AddCheck<StorageHealthCheck>("storage", tags: ["ready"])
            .AddCheck<SqliteHealthCheck>("sqlite", tags: ["ready"])
            .AddCheck<MediaProbeHealthCheck>("ffprobe", tags: ["ready"])
            .AddCheck<LibraryRootsHealthCheck>("media-roots", tags: ["ready"])
            .AddCheck<TmdbConfigurationHealthCheck>("tmdb", tags: ["ready"]);

        return services;
    }

    private static void AddValidated<TOptions, TValidator>(
        IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateOnStart();
    }
}
