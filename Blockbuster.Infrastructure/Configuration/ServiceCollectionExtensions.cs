using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Blockbuster.Core.Persistence;
using Blockbuster.Infrastructure.Health;
using Blockbuster.Infrastructure.Persistence;
using Blockbuster.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Blockbuster.Infrastructure.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBlockbusterConfiguration(
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
        services.AddSingleton<IStoragePathResolver, StoragePathResolver>();
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<IDbConnectionFactory>(provider => provider.GetRequiredService<SqliteConnectionFactory>());
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<IDatabaseMigrator>(provider => provider.GetRequiredService<DatabaseMigrator>());
        services.AddHostedService(provider => provider.GetRequiredService<DatabaseMigrator>());

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
        where TValidator : class, Microsoft.Extensions.Options.IValidateOptions<TOptions>
    {
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<TOptions>, TValidator>();
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateOnStart();
    }
}
