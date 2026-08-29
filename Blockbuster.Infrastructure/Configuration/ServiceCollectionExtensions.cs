using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
