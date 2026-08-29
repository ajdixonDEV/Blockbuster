using Blockbuster.Core.Media;

namespace Blockbuster.Core.Movies;

public sealed record ParsedMovieFileName(string Title, int? Year);

public sealed record MovieMetadataCandidate(
    int TmdbId,
    string Title,
    int? Year,
    string? Overview,
    string? PosterPath,
    string? BackdropPath);

public sealed record MovieMetadata(
    int TmdbId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    TimeSpan? Runtime,
    string? PosterPath,
    string? BackdropPath,
    IReadOnlyList<string> Genres);

public enum MovieMatchOutcome
{
    Accepted,
    MissingYear,
    Unmatched,
    Ambiguous,
    ProviderUnavailable,
    ProbeFailed
}

public sealed record MovieMatchDecision(
    MovieMatchOutcome Outcome,
    MovieMetadataCandidate? Accepted,
    IReadOnlyList<MovieMetadataCandidate> Candidates,
    string Explanation);

public sealed record PendingMovieMatch(
    Guid MediaFileId,
    string RelativePath,
    string ParsedTitle,
    int? ParsedYear,
    MovieMatchOutcome Outcome,
    string Explanation,
    IReadOnlyList<MovieMetadataCandidate> Candidates,
    DateTimeOffset UpdatedAt);

public sealed record MovieScanFile(
    Guid Id,
    string LibrarySourceId,
    string RootPath,
    string NormalizedRelativePath,
    long Length,
    DateTimeOffset LastModified,
    bool IsAvailable,
    string? ProbeError,
    bool IsAssociated);

public sealed record MediaFileUpsert(
    string LibrarySourceId,
    string RootPath,
    string RelativePath,
    string NormalizedRelativePath,
    long Length,
    DateTimeOffset LastModified,
    MediaProbeResult? Probe,
    string? ProbeError);

public sealed record LibraryScanRun(
    Guid Id,
    string LibrarySourceId,
    string RootPath,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool Succeeded,
    int DiscoveredFiles,
    int ChangedFiles,
    int MissingFiles,
    string? Error);

public interface IMovieMetadataProvider
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<MovieMetadataCandidate>> SearchAsync(string title, int year, CancellationToken cancellationToken = default);
    Task<MovieMetadata?> GetAsync(int tmdbId, CancellationToken cancellationToken = default);
}

public interface IArtworkCache
{
    Task<string?> CacheAsync(string kind, int tmdbId, string? providerPath, CancellationToken cancellationToken = default);
}

public interface IMovieCatalogStore
{
    Task<MovieScanFile?> FindFileAsync(string librarySourceId, string rootPath, string normalizedRelativePath, CancellationToken cancellationToken = default);
    Task<MovieScanFile> UpsertFileAsync(MediaFileUpsert file, CancellationToken cancellationToken = default);
    Task<int> MarkMissingAsync(string librarySourceId, string rootPath, IReadOnlyCollection<string> seenNormalizedPaths, CancellationToken cancellationToken = default);
    Task<Guid> StartScanRunAsync(string librarySourceId, string rootPath, DateTimeOffset startedAt, CancellationToken cancellationToken = default);
    Task CompleteScanRunAsync(Guid runId, bool succeeded, int discoveredFiles, int changedFiles, int missingFiles, string? failureMessage, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryScanRun>> ListScanRunsAsync(int limit, CancellationToken cancellationToken = default);
    Task QueuePendingMatchAsync(Guid mediaFileId, ParsedMovieFileName parsed, MovieMatchDecision decision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingMovieMatch>> ListPendingMatchesAsync(CancellationToken cancellationToken = default);
    Task ApplyMetadataAsync(Guid mediaFileId, MovieMetadata metadata, string? localPosterPath, string? localBackdropPath, CancellationToken cancellationToken = default);
    Task ApplyLocalOverrideAsync(Guid mediaFileId, string title, int? year, CancellationToken cancellationToken = default);
    Task ClearPendingMatchAsync(Guid mediaFileId, CancellationToken cancellationToken = default);
}
