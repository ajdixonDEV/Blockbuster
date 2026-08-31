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
    bool IsConfigured
    {
        get;
    }
    Task<IReadOnlyList<MovieMetadataCandidate>> SearchAsync(string title, int year, CancellationToken cancellationToken = default);
    Task<MovieMetadata?> GetAsync(int tmdbId, CancellationToken cancellationToken = default);
}

public interface IArtworkCache
{
    Task<string?> CacheAsync(string kind, int tmdbId, string? providerPath, CancellationToken cancellationToken = default);
}

public sealed record MovieResolutionResult(bool Succeeded, bool PendingReview, string? Message);

/// <summary>Owns administrator-selected movie-match transitions.</summary>
public interface IMovieMatchResolver
{
    Task<MovieResolutionResult> ResolveProviderSelectionAsync(
        Guid mediaFileId,
        int tmdbId,
        CancellationToken cancellationToken = default);

    Task<MovieResolutionResult> ResolveLocalMetadataAsync(
        Guid mediaFileId,
        string title,
        int? year,
        CancellationToken cancellationToken = default);
}

/// <summary>Persists only movie-match and association transitions.</summary>
public interface IMovieMatchTransitionStore
{
    Task ApplyPendingMatchAsync(
        Guid mediaFileId,
        ParsedMovieFileName parsed,
        MovieMatchDecision decision,
        CancellationToken cancellationToken = default);

    Task ApplyMetadataAssociationAsync(
        Guid mediaFileId,
        MovieMetadata metadata,
        string? localPosterPath,
        string? localBackdropPath,
        CancellationToken cancellationToken = default);

    Task ApplyLocalAssociationAsync(
        Guid mediaFileId,
        string title,
        int? year,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only catalog queries used by pages and operator views.</summary>
public interface IMovieCatalogReader
{
    Task<IReadOnlyList<LibraryScanRun>> ListScanRunsAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingMovieMatch>> ListPendingMatchesAsync(CancellationToken cancellationToken = default);
}
