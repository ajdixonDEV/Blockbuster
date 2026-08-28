namespace Blockbuster.Core.Media;

public sealed record MediaFile(
    Guid Id,
    string LibrarySourceId,
    MediaKind Kind,
    string RelativePath,
    long Length,
    DateTimeOffset LastModified,
    bool IsAvailable);
