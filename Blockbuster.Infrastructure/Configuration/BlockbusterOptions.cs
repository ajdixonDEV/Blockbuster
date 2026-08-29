namespace Blockbuster.Infrastructure.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string DataRoot { get; set; } = string.Empty;
    public string? DatabasePath { get; set; }
    public string? ArtworkPath { get; set; }
    public string? CachePath { get; set; }
    public string? GeneratedPath { get; set; }
    public string? LogsPath { get; set; }
    public string? BackupsPath { get; set; }
    public string? DataProtectionKeysPath { get; set; }
}

public sealed class LibrariesOptions
{
    public const string SectionName = "Libraries";

    public List<LibrarySourceOptions> Sources { get; set; } = [];
}

public sealed class LibrarySourceOptions
{
    public string Id { get; set; } = string.Empty;
    public List<string> MovieRoots { get; set; } = [];
}

public sealed class ScanningOptions
{
    public const string SectionName = "Scanning";

    public bool ScanOnStartup { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
    public List<string> Extensions { get; set; } = [".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov"];
    public int Concurrency { get; set; } = 2;
}

public sealed class MediaProbeOptions
{
    public const string SectionName = "MediaProbe";

    public string ExecutablePath { get; set; } = "ffprobe";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class TmdbOptions
{
    public const string SectionName = "Tmdb";

    public string Locale { get; set; } = "en-US";
    public string PosterSize { get; set; } = "w500";
    public string BackdropSize { get; set; } = "w1280";
    public bool RequireMatchingYear { get; set; } = true;
    public bool RequireUniqueMatch { get; set; } = true;
    public string? Token { get; set; }
}

public sealed class PlaybackOptions
{
    public const string SectionName = "Playback";

    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ResumeThreshold { get; set; } = TimeSpan.FromSeconds(30);
    public bool PreferBrowserCompatibleVersions { get; set; } = true;
}

public sealed class HistoryOptions
{
    public const string SectionName = "History";

    public int MaximumEventsPerProfile { get; set; } = 100;
}

public sealed class RoomsOptions
{
    public const string SectionName = "Rooms";

    public TimeSpan EmptyRoomExpiry { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan DriftCheckInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan RateCorrectionThreshold { get; set; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan HardSeekThreshold { get; set; } = TimeSpan.FromSeconds(3);
}

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public TimeSpan AdminCookieLifetime { get; set; } = TimeSpan.FromHours(8);
    public string? BootstrapPin { get; set; }
}
