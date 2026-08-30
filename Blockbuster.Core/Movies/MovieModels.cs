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
    bool IsAssociated,
    bool HasUsableProbeFacts);

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

public sealed record StagedScanPromotion(Guid MediaFileId, string RelativePath, bool IsChanged, bool IsAssociated, string? ProbeError);

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

public sealed record MovieResolutionResult(bool Succeeded, bool PendingReview, string? Message);

/// <summary>Provider work prepared during root observation and applied at promotion.</summary>
public sealed record ScanMatchResolution(
    ParsedMovieFileName Parsed,
    MovieMatchDecision? PendingDecision,
    MovieMetadata? Metadata,
    string? LocalPosterPath,
    string? LocalBackdropPath);

/// <summary>Owns provider matching and the resulting catalog transition for a media file.</summary>
public interface IMovieMatchResolver
{
    Task<ScanMatchResolution> PrepareAutomaticAsync(ParsedMovieFileName parsed, CancellationToken cancellationToken = default);
    Task<MovieResolutionResult> ResolveAutomaticAsync(Guid mediaFileId, ParsedMovieFileName parsed, CancellationToken cancellationToken = default);
    Task<MovieResolutionResult> ResolveProviderSelectionAsync(Guid mediaFileId, int tmdbId, CancellationToken cancellationToken = default);
    Task<MovieResolutionResult> ResolveLocalMetadataAsync(Guid mediaFileId, string title, int? year, CancellationToken cancellationToken = default);
}

/// <summary>Internal catalog mutations used by scanning and match resolution.</summary>
public interface IMovieCatalogStore
{
    Task<MovieScanFile?> FindFileAsync(string librarySourceId, string rootPath, string normalizedRelativePath, CancellationToken cancellationToken = default);
    Task<MovieScanFile> UpsertFileAsync(MediaFileUpsert file, CancellationToken cancellationToken = default);
    Task<int> MarkMissingAsync(string librarySourceId, string rootPath, IReadOnlyCollection<string> seenNormalizedPaths, CancellationToken cancellationToken = default);
    Task<Guid> StartScanRunAsync(string librarySourceId, string rootPath, DateTimeOffset startedAt, CancellationToken cancellationToken = default);
    Task CompleteScanRunAsync(Guid runId, bool succeeded, int discoveredFiles, int changedFiles, int missingFiles, string? failureMessage, CancellationToken cancellationToken = default);
    Task<(int MissingFiles, IReadOnlyList<StagedScanPromotion> Files)> PromoteStagedRunAsync(Guid runId, string librarySourceId, string rootPath, int discoveredFiles, int changedFiles, CancellationToken cancellationToken = default);
    Task QueuePendingMatchAsync(Guid mediaFileId, ParsedMovieFileName parsed, MovieMatchDecision decision, CancellationToken cancellationToken = default);
    Task ApplyMetadataAsync(Guid mediaFileId, MovieMetadata metadata, string? localPosterPath, string? localBackdropPath, CancellationToken cancellationToken = default);
    Task ApplyLocalOverrideAsync(Guid mediaFileId, string title, int? year, CancellationToken cancellationToken = default);
    Task ClearPendingMatchAsync(Guid mediaFileId, CancellationToken cancellationToken = default);
}

/// <summary>Read-only catalog queries used by pages and operator views.</summary>
public interface IMovieCatalogReader
{
    Task<IReadOnlyList<LibraryScanRun>> ListScanRunsAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingMovieMatch>> ListPendingMatchesAsync(CancellationToken cancellationToken = default);
}
