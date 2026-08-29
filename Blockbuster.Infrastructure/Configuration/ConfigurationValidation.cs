using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Configuration;

internal static class ConfigurationValidation
{
    public static bool IsAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    public static bool IsFourDigitPin(string? pin) =>
        pin is { Length: 4 } && pin.All(static character => character is >= '0' and <= '9');

    public static ValidateOptionsResult Result(List<string> failures) =>
        failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
}

internal sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var failures = new List<string>();
        if (!ConfigurationValidation.IsAbsolutePath(options.DataRoot))
        {
            failures.Add("Storage:DataRoot must be an absolute path.");
        }

        ValidateOverride(options.DatabasePath, "DatabasePath", failures);
        ValidateOverride(options.ArtworkPath, "ArtworkPath", failures);
        ValidateOverride(options.CachePath, "CachePath", failures);
        ValidateOverride(options.GeneratedPath, "GeneratedPath", failures);
        ValidateOverride(options.LogsPath, "LogsPath", failures);
        ValidateOverride(options.BackupsPath, "BackupsPath", failures);
        ValidateOverride(options.DataProtectionKeysPath, "DataProtectionKeysPath", failures);
        return ConfigurationValidation.Result(failures);
    }

    private static void ValidateOverride(string? value, string key, List<string> failures)
    {
        if (value is not null && !ConfigurationValidation.IsAbsolutePath(value))
        {
            failures.Add($"Storage:{key} must be an absolute path when supplied.");
        }
    }
}

internal sealed class LibrariesOptionsValidator : IValidateOptions<LibrariesOptions>
{
    public ValidateOptionsResult Validate(string? name, LibrariesOptions options)
    {
        var failures = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in options.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
            {
                failures.Add("Every Libraries source must have a stable, non-empty Id.");
            }
            else if (!ids.Add(source.Id))
            {
                failures.Add($"Libraries source Id '{source.Id}' is duplicated.");
            }

            if (source.MovieRoots.Count == 0)
            {
                failures.Add($"Libraries source '{source.Id}' must contain at least one movie root.");
            }

            foreach (var root in source.MovieRoots.Where(root => !ConfigurationValidation.IsAbsolutePath(root)))
            {
                failures.Add($"Movie root '{root}' for Libraries source '{source.Id}' must be an absolute path.");
            }
        }

        return ConfigurationValidation.Result(failures);
    }
}

internal sealed class ScanningOptionsValidator : IValidateOptions<ScanningOptions>
{
    public ValidateOptionsResult Validate(string? name, ScanningOptions options)
    {
        var failures = new List<string>();
        if (options.Interval <= TimeSpan.Zero) failures.Add("Scanning:Interval must be positive.");
        if (options.Concurrency <= 0) failures.Add("Scanning:Concurrency must be positive.");
        if (options.Extensions.Count == 0) failures.Add("Scanning:Extensions must not be empty.");
        if (options.Extensions.Any(extension => string.IsNullOrWhiteSpace(extension) || extension[0] != '.'))
        {
            failures.Add("Every Scanning extension must begin with a period.");
        }

        return ConfigurationValidation.Result(failures);
    }
}

internal sealed class MediaProbeOptionsValidator : IValidateOptions<MediaProbeOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaProbeOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ExecutablePath)) failures.Add("MediaProbe:ExecutablePath is required.");
        if (options.Timeout <= TimeSpan.Zero) failures.Add("MediaProbe:Timeout must be positive.");
        return ConfigurationValidation.Result(failures);
    }
}

internal sealed class TmdbOptionsValidator : IValidateOptions<TmdbOptions>
{
    public ValidateOptionsResult Validate(string? name, TmdbOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Locale)) failures.Add("Tmdb:Locale is required.");
        if (string.IsNullOrWhiteSpace(options.PosterSize)) failures.Add("Tmdb:PosterSize is required.");
        if (string.IsNullOrWhiteSpace(options.BackdropSize)) failures.Add("Tmdb:BackdropSize is required.");
        return ConfigurationValidation.Result(failures);
    }
}

internal sealed class PlaybackOptionsValidator : IValidateOptions<PlaybackOptions>
{
    public ValidateOptionsResult Validate(string? name, PlaybackOptions options)
    {
        var failures = new List<string>();
        if (options.ProgressInterval <= TimeSpan.Zero) failures.Add("Playback:ProgressInterval must be positive.");
        if (options.ResumeThreshold < TimeSpan.Zero) failures.Add("Playback:ResumeThreshold cannot be negative.");
        return ConfigurationValidation.Result(failures);
    }
}

internal sealed class HistoryOptionsValidator : IValidateOptions<HistoryOptions>
{
    public ValidateOptionsResult Validate(string? name, HistoryOptions options) =>
        options.MaximumEventsPerProfile is >= 1 and <= 100
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("History:MaximumEventsPerProfile must be between 1 and 100.");
}

internal sealed class RoomsOptionsValidator : IValidateOptions<RoomsOptions>
{
    public ValidateOptionsResult Validate(string? name, RoomsOptions options)
    {
        var failures = new List<string>();
        if (options.EmptyRoomExpiry <= TimeSpan.Zero) failures.Add("Rooms:EmptyRoomExpiry must be positive.");
        if (options.DriftCheckInterval <= TimeSpan.Zero) failures.Add("Rooms:DriftCheckInterval must be positive.");
        if (options.RateCorrectionThreshold <= TimeSpan.Zero) failures.Add("Rooms:RateCorrectionThreshold must be positive.");
        if (options.HardSeekThreshold <= options.RateCorrectionThreshold)
        {
            failures.Add("Rooms:HardSeekThreshold must exceed RateCorrectionThreshold.");
        }

        return ConfigurationValidation.Result(failures);
    }
}

internal sealed class AuthenticationOptionsValidator : IValidateOptions<AuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        var failures = new List<string>();
        if (options.AdminCookieLifetime <= TimeSpan.Zero) failures.Add("Authentication:AdminCookieLifetime must be positive.");
        if (options.BootstrapPin is not null && !ConfigurationValidation.IsFourDigitPin(options.BootstrapPin))
        {
            failures.Add("Authentication:BootstrapPin must contain exactly four digits when supplied.");
        }

        return ConfigurationValidation.Result(failures);
    }
}

internal sealed class ReverseProxyOptionsValidator : IValidateOptions<ReverseProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ReverseProxyOptions options)
    {
        var failures = new List<string>();
        if (options.ForwardLimit is < 1 or > 5) failures.Add("ReverseProxy:ForwardLimit must be between 1 and 5.");
        if (options.Enabled && options.KnownProxies.Count == 0)
            failures.Add("ReverseProxy:KnownProxies must contain at least one trusted proxy when forwarding is enabled.");
        foreach (var proxy in options.KnownProxies.Where(proxy => !System.Net.IPAddress.TryParse(proxy, out _)))
            failures.Add($"ReverseProxy known proxy '{proxy}' must be an IP address.");
        return ConfigurationValidation.Result(failures);
    }
}
