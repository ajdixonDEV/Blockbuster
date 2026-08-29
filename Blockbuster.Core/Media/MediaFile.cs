namespace Blockbuster.Core.Media;

public sealed record MediaFile(
    Guid Id,
    string LibrarySourceId,
    MediaKind Kind,
    string RelativePath,
    long Length,
    DateTimeOffset LastModified,
    bool IsAvailable,
    string RootPath = "",
    TimeSpan? Duration = null,
    string? Container = null,
    string? VideoCodec = null,
    string? AudioCodec = null,
    int? Width = null,
    int? Height = null,
    int? AudioChannels = null,
    string? ProbeError = null);
