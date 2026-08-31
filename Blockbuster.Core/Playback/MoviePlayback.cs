namespace Blockbuster.Core.Playback;

public sealed record MovieCatalogQuery(
    string? Search = null,
    string? Genre = null,
    int? Year = null,
    MovieSort Sort = MovieSort.Title,
    int Page = 1,
    int PageSize = 24);

public enum MovieSort
{
    Title, YearDescending, RecentlyAdded
}

public sealed record MovieCatalogItem(
    Guid Id, string Title, int? Year, string? Overview, string? PosterUrl,
    IReadOnlyList<string> Genres, int AvailableVersions, DateTimeOffset AddedAt,
    TimeSpan? Progress, TimeSpan? Duration);

public sealed record MovieCatalogPage(
    IReadOnlyList<MovieCatalogItem> Items, int Total, int Page, int PageSize,
    IReadOnlyList<string> Genres, IReadOnlyList<int> Years);

public sealed record MovieVersion(
    Guid MediaFileId, string Quality, string FileName, string Container, string? VideoCodec,
    string? AudioCodec, int? Width, int? Height, int? AudioChannels,
    TimeSpan? Duration, long Length, DateTimeOffset LastModified,
    bool IsAvailable, bool IsBrowserCompatible, string CompatibilityExplanation);

public sealed record MovieDetails(
    Guid Id, string Title, int? Year, string? OriginalTitle, string? Overview,
    string? PosterUrl, string? BackdropUrl, IReadOnlyList<string> Genres,
    IReadOnlyList<MovieVersion> Versions, TimeSpan? Progress, long ProgressRevision);

public sealed record MediaStreamSource(
    Guid MediaFileId, Guid MovieId, string FullPath, string ContentType,
    long Length, DateTimeOffset LastModified);
public sealed record ArtworkSource(string FullPath, string ContentType, DateTimeOffset LastModified);

public sealed record PlaybackProgress(Guid ProfileId, Guid MovieId, TimeSpan Position, long Revision, DateTimeOffset UpdatedAt);
public sealed record PlaybackProgressResult(bool Accepted, PlaybackProgress Current);
public sealed record PlaybackEvent(
    Guid Id,
    Guid ProfileId,
    Guid MovieId,
    string MovieTitle,
    string EventType,
    TimeSpan Position,
    DateTimeOffset OccurredAt);

public interface IMovieLibrary
{
    Task<MovieCatalogPage> BrowseAsync(Guid profileId, MovieCatalogQuery query, CancellationToken cancellationToken = default);
    Task<MovieDetails?> GetAsync(Guid movieId, Guid profileId, CancellationToken cancellationToken = default);
    Task<MediaStreamSource?> AuthorizeStreamAsync(Guid mediaFileId, CancellationToken cancellationToken = default);
    Task<ArtworkSource?> GetArtworkAsync(Guid movieId, string kind, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaybackEvent>> RecentActivityAsync(Guid profileId, int limit, CancellationToken cancellationToken = default);
}

public interface IPlaybackProgressStore
{
    Task<PlaybackProgress?> GetProgressAsync(Guid profileId, Guid movieId, CancellationToken cancellationToken = default);
    Task<PlaybackProgressResult> SaveAsync(
        Guid profileId,
        Guid movieId,
        TimeSpan position,
        long expectedRevision,
        string eventType,
        CancellationToken cancellationToken = default);
}
