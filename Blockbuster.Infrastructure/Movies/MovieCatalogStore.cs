using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Blockbuster.Core.Media;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Persistence;
using Dapper;

namespace Blockbuster.Infrastructure.Movies;

internal sealed record CatalogFileState(
    Guid Id,
    long Length,
    DateTimeOffset LastModified,
    bool IsAvailable,
    bool IsAssociated,
    bool HasUsableProbeFacts);

internal sealed record StagedCatalogObservation(
    string RelativePath,
    string NormalizedPath,
    long Length,
    DateTimeOffset LastModified,
    MediaProbeResult? Probe,
    string? ProbeError,
    Guid AssignedMediaFileId,
    string? MatchResolutionJson);

public sealed class MovieCatalogStore(IDbConnectionFactory connections) :
    IMovieMatchTransitionStore,
    IMovieCatalogReader
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal async Task<CatalogFileState?> FindFileAsync(
        string librarySourceId,
        string rootPath,
        string normalizedRelativePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MediaFileRow>(
            new CommandDefinition(
                """
                SELECT
                    id,
                    length,
                    last_modified_at AS LastModifiedAt,
                    is_available AS IsAvailable,
                    duration_seconds AS DurationSeconds,
                    container,
                    video_codec AS VideoCodec,
                    EXISTS(
                        SELECT 1
                        FROM movie_versions AS version
                        WHERE version.media_file_id = media_files.id
                    ) AS IsAssociated
                FROM media_files
                WHERE library_source_id = @LibrarySourceId
                  AND root_path = @RootPath
                  AND normalized_relative_path = @NormalizedRelativePath
                """,
                new
                {
                    LibrarySourceId = librarySourceId,
                    RootPath = rootPath,
                    NormalizedRelativePath = normalizedRelativePath
                },
                cancellationToken: cancellationToken));
        return row is null
            ? null
            : new CatalogFileState(
                Guid.ParseExact(row.Id, "N"),
                row.Length,
                ParseDate(row.LastModifiedAt),
                row.IsAvailable != 0,
                row.IsAssociated != 0,
                row.DurationSeconds is not null
                    && !string.IsNullOrWhiteSpace(row.Container)
                    && !string.IsNullOrWhiteSpace(row.VideoCodec));
    }

    internal async Task<Guid> StartScanRunAsync(
        string librarySourceId,
        string rootPath,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var started = FormatDate(startedAt);
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO library_scan_runs(
                    id,
                    library_source_id,
                    root_path,
                    started_at
                )
                VALUES(
                    @Id,
                    @LibrarySourceId,
                    @RootPath,
                    @StartedAt
                );
                """,
                new
                {
                    Id = id.ToString("N"),
                    LibrarySourceId = librarySourceId,
                    RootPath = rootPath,
                    StartedAt = started
                },
                transaction,
                cancellationToken: cancellationToken));
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO configured_library_scan_state(
                    library_source_id,
                    root_path,
                    last_started_at
                )
                VALUES(
                    @LibrarySourceId,
                    @RootPath,
                    @StartedAt
                )
                ON CONFLICT(library_source_id, root_path) DO UPDATE SET
                    last_started_at = excluded.last_started_at
                """,
                new
                {
                    LibrarySourceId = librarySourceId,
                    RootPath = rootPath,
                    StartedAt = started
                },
                transaction,
                cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    internal async Task StageObservationsAsync(
        Guid runId,
        IEnumerable<StagedCatalogObservation> observations,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO library_scan_observations(
                run_id,
                normalized_relative_path,
                relative_path,
                length,
                last_modified_at,
                duration_seconds,
                container,
                video_codec,
                audio_codec,
                width,
                height,
                audio_channels,
                probe_error,
                assigned_media_file_id,
                match_resolution_json
            )
            VALUES(
                @RunId,
                @NormalizedPath,
                @RelativePath,
                @Length,
                @LastModifiedAt,
                @DurationSeconds,
                @Container,
                @VideoCodec,
                @AudioCodec,
                @Width,
                @Height,
                @AudioChannels,
                @ProbeError,
                @AssignedMediaFileId,
                @MatchResolutionJson
            )
            """;

        var parameters = new ObservationParameters(command);
        await command.PrepareAsync(cancellationToken);
        foreach (var observation in observations)
        {
            parameters.Set(runId, observation);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<int> PromoteStagedRunAsync(
        Guid runId,
        string librarySourceId,
        string rootPath,
        int discoveredFiles,
        int changedFiles,
        CancellationToken cancellationToken = default)
    {
        var now = FormatDate(DateTimeOffset.UtcNow);
        var runIdText = runId.ToString("N");
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await PromoteMediaFactsAsync(
            connection,
            transaction,
            runIdText,
            librarySourceId,
            rootPath,
            now,
            cancellationToken);

        var transitions = await connection.QueryAsync<StagedTransitionRow>(
            new CommandDefinition(
                """
                SELECT
                    file.id AS MediaFileId,
                    observation.relative_path AS RelativePath,
                    observation.probe_error AS ProbeError,
                    observation.match_resolution_json AS MatchResolutionJson
                FROM library_scan_observations AS observation
                JOIN media_files AS file
                  ON file.library_source_id = @LibrarySourceId
                 AND file.root_path = @RootPath
                 AND file.normalized_relative_path =
                     observation.normalized_relative_path
                WHERE observation.run_id = @RunId
                  AND (
                    observation.probe_error IS NOT NULL
                    OR observation.match_resolution_json IS NOT NULL
                  )
                ORDER BY observation.normalized_relative_path
                """,
                new
                {
                    RunId = runIdText,
                    LibrarySourceId = librarySourceId,
                    RootPath = rootPath
                },
                transaction,
                cancellationToken: cancellationToken));

        foreach (var row in transitions)
        {
            var transition = CreateStagedTransition(row);
            await WriteTransitionAsync(
                connection,
                transaction,
                Guid.ParseExact(row.MediaFileId, "N"),
                transition,
                now,
                cancellationToken);
        }

        var missingFiles = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE media_files
                SET is_available = 0
                WHERE library_source_id = @LibrarySourceId
                  AND root_path = @RootPath
                  AND is_available = 1
                  AND normalized_relative_path NOT IN (
                    SELECT normalized_relative_path
                    FROM library_scan_observations
                    WHERE run_id = @RunId
                  )
                """,
                new
                {
                    LibrarySourceId = librarySourceId,
                    RootPath = rootPath,
                    RunId = runIdText
                },
                transaction,
                cancellationToken: cancellationToken));

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE library_scan_runs
                SET completed_at = @Now,
                    succeeded = 1,
                    discovered_files = @DiscoveredFiles,
                    changed_files = @ChangedFiles,
                    missing_files = @MissingFiles,
                    error = NULL
                WHERE id = @RunId;

                UPDATE configured_library_scan_state
                SET last_completed_at = @Now,
                    last_succeeded = 1,
                    last_error = NULL
                WHERE (library_source_id, root_path) = (
                    SELECT library_source_id, root_path
                    FROM library_scan_runs
                    WHERE id = @RunId
                );

                DELETE FROM library_scan_observations
                WHERE run_id = @RunId;
                """,
                new
                {
                    RunId = runIdText,
                    Now = now,
                    DiscoveredFiles = discoveredFiles,
                    ChangedFiles = changedFiles,
                    MissingFiles = missingFiles
                },
                transaction,
                cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return missingFiles;
    }

    internal async Task FailScanRunAndClearStagingAsync(
        Guid runId,
        int discoveredFiles,
        int changedFiles,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        var now = FormatDate(DateTimeOffset.UtcNow);
        var runIdText = runId.ToString("N");
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE library_scan_runs
                SET completed_at = @Now,
                    succeeded = 0,
                    discovered_files = @DiscoveredFiles,
                    changed_files = @ChangedFiles,
                    missing_files = 0,
                    error = @Error
                WHERE id = @RunId;

                UPDATE configured_library_scan_state
                SET last_completed_at = @Now,
                    last_succeeded = 0,
                    last_error = @Error
                WHERE (library_source_id, root_path) = (
                    SELECT library_source_id, root_path
                    FROM library_scan_runs
                    WHERE id = @RunId
                );

                DELETE FROM library_scan_observations
                WHERE run_id = @RunId;
                """,
                new
                {
                    RunId = runIdText,
                    Now = now,
                    DiscoveredFiles = discoveredFiles,
                    ChangedFiles = changedFiles,
                    Error = failureMessage
                },
                transaction,
                cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task RecoverInterruptedRunsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = FormatDate(DateTimeOffset.UtcNow);
        const string message = "Scan interrupted by application restart.";
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE configured_library_scan_state
                SET last_completed_at = @Now,
                    last_succeeded = 0,
                    last_error = @Error
                WHERE EXISTS (
                    SELECT 1
                    FROM library_scan_runs AS run
                    WHERE run.completed_at IS NULL
                      AND run.library_source_id =
                          configured_library_scan_state.library_source_id
                      AND run.root_path = configured_library_scan_state.root_path
                );

                UPDATE library_scan_runs
                SET completed_at = @Now,
                    succeeded = 0,
                    error = @Error
                WHERE completed_at IS NULL;

                DELETE FROM library_scan_observations
                WHERE run_id IN (
                    SELECT id
                    FROM library_scan_runs
                    WHERE completed_at = @Now
                      AND succeeded = 0
                      AND error = @Error
                );
                """,
                new
                {
                    Now = now,
                    Error = message
                },
                transaction,
                cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public Task ApplyPendingMatchAsync(
        Guid mediaFileId,
        ParsedMovieFileName parsed,
        MovieMatchDecision decision,
        CancellationToken cancellationToken = default)
    {
        return ApplyTransitionAsync(
            mediaFileId,
            CatalogTransition.Pending(parsed, decision),
            cancellationToken);
    }

    public Task ApplyMetadataAssociationAsync(
        Guid mediaFileId,
        MovieMetadata metadata,
        string? localPosterPath,
        string? localBackdropPath,
        CancellationToken cancellationToken = default)
    {
        return ApplyTransitionAsync(
            mediaFileId,
            CatalogTransition.ForMetadata(
                metadata,
                localPosterPath,
                localBackdropPath),
            cancellationToken);
    }

    public Task ApplyLocalAssociationAsync(
        Guid mediaFileId,
        string title,
        int? year,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = ValidateLocalMetadata(title, year);
        return ApplyTransitionAsync(
            mediaFileId,
            CatalogTransition.Local(normalizedTitle, year),
            cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryScanRun>> ListScanRunsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ScanRunRow>(
            new CommandDefinition(
                """
                SELECT
                    id,
                    library_source_id AS LibrarySourceId,
                    root_path AS RootPath,
                    started_at AS StartedAt,
                    completed_at AS CompletedAt,
                    succeeded,
                    discovered_files AS DiscoveredFiles,
                    changed_files AS ChangedFiles,
                    missing_files AS MissingFiles,
                    error
                FROM library_scan_runs
                ORDER BY started_at DESC
                LIMIT @Limit
                """,
                new
                {
                    Limit = Math.Clamp(limit, 1, 100)
                },
                cancellationToken: cancellationToken));
        return rows
            .Select(row => new LibraryScanRun(
                Guid.ParseExact(row.Id, "N"),
                row.LibrarySourceId,
                row.RootPath,
                ParseDate(row.StartedAt),
                row.CompletedAt is null ? null : ParseDate(row.CompletedAt),
                row.Succeeded != 0,
                checked((int)row.DiscoveredFiles),
                checked((int)row.ChangedFiles),
                checked((int)row.MissingFiles),
                row.Error))
            .ToList();
    }

    public async Task<IReadOnlyList<PendingMovieMatch>> ListPendingMatchesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PendingRow>(
            new CommandDefinition(
                """
                SELECT
                    pending.media_file_id AS MediaFileId,
                    file.relative_path AS RelativePath,
                    pending.parsed_title AS ParsedTitle,
                    pending.parsed_year AS ParsedYear,
                    pending.outcome,
                    pending.explanation,
                    pending.candidates_json AS CandidatesJson,
                    pending.updated_at AS UpdatedAt
                FROM pending_movie_matches AS pending
                JOIN media_files AS file
                  ON file.id = pending.media_file_id
                ORDER BY pending.updated_at DESC
                """,
                cancellationToken: cancellationToken));
        return rows
            .Select(row => new PendingMovieMatch(
                Guid.ParseExact(row.MediaFileId, "N"),
                row.RelativePath,
                row.ParsedTitle,
                row.ParsedYear is null
                    ? null
                    : checked((int)row.ParsedYear.Value),
                (MovieMatchOutcome)checked((int)row.Outcome),
                row.Explanation,
                JsonSerializer.Deserialize<List<MovieMetadataCandidate>>(
                    row.CandidatesJson,
                    JsonOptions) ?? [],
                ParseDate(row.UpdatedAt)))
            .ToList();
    }

    private static async Task PromoteMediaFactsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string runId,
        string librarySourceId,
        string rootPath,
        string now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO media_files(
                    id,
                    library_source_id,
                    root_path,
                    media_kind,
                    relative_path,
                    normalized_relative_path,
                    length,
                    last_modified_at,
                    duration_seconds,
                    container,
                    video_codec,
                    audio_codec,
                    width,
                    height,
                    audio_channels,
                    probe_error,
                    is_available,
                    first_seen_at,
                    last_seen_at
                )
                SELECT
                    observation.assigned_media_file_id,
                    @LibrarySourceId,
                    @RootPath,
                    @MediaKind,
                    observation.relative_path,
                    observation.normalized_relative_path,
                    observation.length,
                    observation.last_modified_at,
                    observation.duration_seconds,
                    observation.container,
                    observation.video_codec,
                    observation.audio_codec,
                    observation.width,
                    observation.height,
                    observation.audio_channels,
                    observation.probe_error,
                    1,
                    @Now,
                    @Now
                FROM library_scan_observations AS observation
                WHERE observation.run_id = @RunId
                ON CONFLICT(
                    library_source_id,
                    root_path,
                    normalized_relative_path
                ) DO UPDATE SET
                    relative_path = excluded.relative_path,
                    length = excluded.length,
                    last_modified_at = excluded.last_modified_at,
                    duration_seconds = CASE
                        WHEN media_files.is_available = 0
                          OR media_files.length <> excluded.length
                          OR media_files.last_modified_at <>
                             excluded.last_modified_at
                          OR excluded.duration_seconds IS NOT NULL
                          OR excluded.probe_error IS NOT NULL
                        THEN excluded.duration_seconds
                        ELSE media_files.duration_seconds
                    END,
                    container = CASE
                        WHEN media_files.is_available = 0
                          OR media_files.length <> excluded.length
                          OR media_files.last_modified_at <>
                             excluded.last_modified_at
                          OR excluded.duration_seconds IS NOT NULL
                          OR excluded.probe_error IS NOT NULL
                        THEN excluded.container
                        ELSE media_files.container
                    END,
                    video_codec = CASE
                        WHEN media_files.is_available = 0
                          OR media_files.length <> excluded.length
                          OR media_files.last_modified_at <>
                             excluded.last_modified_at
                          OR excluded.duration_seconds IS NOT NULL
                          OR excluded.probe_error IS NOT NULL
                        THEN excluded.video_codec
                        ELSE media_files.video_codec
                    END,
                    audio_codec = CASE
                        WHEN media_files.is_available = 0
                          OR media_files.length <> excluded.length
                          OR media_files.last_modified_at <>
                             excluded.last_modified_at
                          OR excluded.duration_seconds IS NOT NULL
                          OR excluded.probe_error IS NOT NULL
                        THEN excluded.audio_codec
                        ELSE media_files.audio_codec
                    END,
                    width = CASE
                        WHEN media_files.is_available = 0
                          OR media_files.length <> excluded.length
                          OR media_files.last_modified_at <>
                             excluded.last_modified_at
                          OR excluded.duration_seconds IS NOT NULL
                          OR excluded.probe_error IS NOT NULL
                        THEN excluded.width
                        ELSE media_files.width
                    END,
                    height = CASE
                        WHEN media_files.is_available = 0
                          OR media_files.length <> excluded.length
                          OR media_files.last_modified_at <>
                             excluded.last_modified_at
                          OR excluded.duration_seconds IS NOT NULL
                          OR excluded.probe_error IS NOT NULL
                        THEN excluded.height
                        ELSE media_files.height
                    END,
                    audio_channels = CASE
                        WHEN media_files.is_available = 0
                          OR media_files.length <> excluded.length
                          OR media_files.last_modified_at <>
                             excluded.last_modified_at
                          OR excluded.duration_seconds IS NOT NULL
                          OR excluded.probe_error IS NOT NULL
                        THEN excluded.audio_channels
                        ELSE media_files.audio_channels
                    END,
                    probe_error = CASE
                        WHEN media_files.is_available = 0
                          OR media_files.length <> excluded.length
                          OR media_files.last_modified_at <>
                             excluded.last_modified_at
                          OR excluded.duration_seconds IS NOT NULL
                          OR excluded.probe_error IS NOT NULL
                        THEN excluded.probe_error
                        ELSE media_files.probe_error
                    END,
                    is_available = 1,
                    last_seen_at = excluded.last_seen_at
                """,
                new
                {
                    RunId = runId,
                    LibrarySourceId = librarySourceId,
                    RootPath = rootPath,
                    MediaKind = (int)MediaKind.Movie,
                    Now = now
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private async Task ApplyTransitionAsync(
        Guid mediaFileId,
        CatalogTransition transition,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await WriteTransitionAsync(
            connection,
            transaction,
            mediaFileId,
            transition,
            FormatDate(DateTimeOffset.UtcNow),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task WriteTransitionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid mediaFileId,
        CatalogTransition transition,
        string now,
        CancellationToken cancellationToken)
    {
        return transition.Kind switch
        {
            CatalogTransitionKind.Pending => WritePendingAsync(
                connection,
                transaction,
                mediaFileId,
                transition.Parsed!,
                transition.Decision!,
                now,
                cancellationToken),
            CatalogTransitionKind.Metadata => WriteMetadataAssociationAsync(
                connection,
                transaction,
                mediaFileId,
                transition.Metadata!,
                transition.LocalPosterPath,
                transition.LocalBackdropPath,
                now,
                cancellationToken),
            CatalogTransitionKind.Local => WriteLocalAssociationAsync(
                connection,
                transaction,
                mediaFileId,
                transition.LocalTitle!,
                transition.LocalYear,
                now,
                cancellationToken),
            _ => throw new InvalidOperationException(
                "The catalog transition kind is unsupported.")
        };
    }

    private static Task<int> WritePendingAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid mediaFileId,
        ParsedMovieFileName parsed,
        MovieMatchDecision decision,
        string now,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO pending_movie_matches(
                    media_file_id,
                    parsed_title,
                    parsed_year,
                    outcome,
                    explanation,
                    candidates_json,
                    updated_at
                )
                VALUES(
                    @MediaFileId,
                    @Title,
                    @Year,
                    @Outcome,
                    @Explanation,
                    @Candidates,
                    @Now
                )
                ON CONFLICT(media_file_id) DO UPDATE SET
                    parsed_title = excluded.parsed_title,
                    parsed_year = excluded.parsed_year,
                    outcome = excluded.outcome,
                    explanation = excluded.explanation,
                    candidates_json = excluded.candidates_json,
                    updated_at = excluded.updated_at
                """,
                new
                {
                    MediaFileId = mediaFileId.ToString("N"),
                    parsed.Title,
                    parsed.Year,
                    Outcome = (int)decision.Outcome,
                    decision.Explanation,
                    Candidates = JsonSerializer.Serialize(
                        decision.Candidates,
                        JsonOptions),
                    Now = now
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task WriteMetadataAssociationAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid mediaFileId,
        MovieMetadata metadata,
        string? localPosterPath,
        string? localBackdropPath,
        string now,
        CancellationToken cancellationToken)
    {
        var proposedMovieId = Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO movies(
                    id,
                    tmdb_id,
                    provider_title,
                    original_title,
                    provider_year,
                    overview,
                    runtime_seconds,
                    poster_provider_path,
                    backdrop_provider_path,
                    local_poster_path,
                    local_backdrop_path,
                    created_at,
                    updated_at
                )
                VALUES(
                    @MovieId,
                    @TmdbId,
                    @Title,
                    @OriginalTitle,
                    @Year,
                    @Overview,
                    @RuntimeSeconds,
                    @PosterPath,
                    @BackdropPath,
                    @LocalPosterPath,
                    @LocalBackdropPath,
                    @Now,
                    @Now
                )
                ON CONFLICT(tmdb_id) WHERE tmdb_id IS NOT NULL DO UPDATE SET
                    provider_title = excluded.provider_title,
                    original_title = excluded.original_title,
                    provider_year = excluded.provider_year,
                    overview = excluded.overview,
                    runtime_seconds = excluded.runtime_seconds,
                    poster_provider_path = excluded.poster_provider_path,
                    backdrop_provider_path = excluded.backdrop_provider_path,
                    local_poster_path = COALESCE(
                        excluded.local_poster_path,
                        movies.local_poster_path
                    ),
                    local_backdrop_path = COALESCE(
                        excluded.local_backdrop_path,
                        movies.local_backdrop_path
                    ),
                    updated_at = excluded.updated_at
                """,
                new
                {
                    MovieId = proposedMovieId,
                    metadata.TmdbId,
                    metadata.Title,
                    metadata.OriginalTitle,
                    metadata.Year,
                    metadata.Overview,
                    RuntimeSeconds = metadata.Runtime?.TotalSeconds,
                    metadata.PosterPath,
                    metadata.BackdropPath,
                    LocalPosterPath = localPosterPath,
                    LocalBackdropPath = localBackdropPath,
                    Now = now
                },
                transaction,
                cancellationToken: cancellationToken));
        var movieId = await connection.QuerySingleAsync<string>(
            new CommandDefinition(
                "SELECT id FROM movies WHERE tmdb_id = @TmdbId",
                new
                {
                    metadata.TmdbId
                },
                transaction,
                cancellationToken: cancellationToken));

        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM movie_genres WHERE movie_id = @MovieId",
                new
                {
                    MovieId = movieId
                },
                transaction,
                cancellationToken: cancellationToken));
        foreach (var genre in metadata.Genres.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO movie_genres(movie_id, genre)
                    VALUES(@MovieId, @Genre)
                    """,
                    new
                    {
                        MovieId = movieId,
                        Genre = genre
                    },
                    transaction,
                    cancellationToken: cancellationToken));
        }

        await ReplaceAssociationAsync(
            connection,
            transaction,
            mediaFileId,
            movieId,
            cancellationToken);
    }

    private static async Task WriteLocalAssociationAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid mediaFileId,
        string title,
        int? year,
        string now,
        CancellationToken cancellationToken)
    {
        var mediaFileIdText = mediaFileId.ToString("N");
        var movieId = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                SELECT movie_id
                FROM movie_versions
                WHERE media_file_id = @MediaFileId
                """,
                new
                {
                    MediaFileId = mediaFileIdText
                },
                transaction,
                cancellationToken: cancellationToken));
        if (movieId is null)
        {
            movieId = Guid.NewGuid().ToString("N");
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO movies(
                        id,
                        provider_title,
                        provider_year,
                        created_at,
                        updated_at
                    )
                    VALUES(
                        @MovieId,
                        @Title,
                        @Year,
                        @Now,
                        @Now
                    );

                    INSERT INTO movie_versions(movie_id, media_file_id)
                    VALUES(@MovieId, @MediaFileId);
                    """,
                    new
                    {
                        MovieId = movieId,
                        Title = title,
                        Year = year,
                        Now = now,
                        MediaFileId = mediaFileIdText
                    },
                    transaction,
                    cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO movie_overrides(movie_id, title, year, updated_at)
                VALUES(@MovieId, @Title, @Year, @Now)
                ON CONFLICT(movie_id) DO UPDATE SET
                    title = excluded.title,
                    year = excluded.year,
                    updated_at = excluded.updated_at;

                DELETE FROM pending_movie_matches
                WHERE media_file_id = @MediaFileId;
                """,
                new
                {
                    MovieId = movieId,
                    Title = title,
                    Year = year,
                    Now = now,
                    MediaFileId = mediaFileIdText
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static Task<int> ReplaceAssociationAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid mediaFileId,
        string movieId,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM movie_versions
                WHERE media_file_id = @MediaFileId;

                INSERT INTO movie_versions(movie_id, media_file_id)
                VALUES(@MovieId, @MediaFileId);

                DELETE FROM pending_movie_matches
                WHERE media_file_id = @MediaFileId;
                """,
                new
                {
                    MovieId = movieId,
                    MediaFileId = mediaFileId.ToString("N")
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static CatalogTransition CreateStagedTransition(
        StagedTransitionRow row)
    {
        var parsed = MovieFilenameParser.Parse(row.RelativePath);
        if (row.ProbeError is not null)
        {
            return CatalogTransition.Pending(
                parsed,
                new MovieMatchDecision(
                    MovieMatchOutcome.ProbeFailed,
                    null,
                    [],
                    "ffprobe could not read this file; inspect the probe error "
                    + "and retry."));
        }

        var prepared = JsonSerializer.Deserialize<PreparedMovieMatch>(
                row.MatchResolutionJson!,
                JsonOptions)
            ?? throw new InvalidDataException(
                "Invalid staged movie-match resolution.");
        return prepared.Metadata is null
            ? CatalogTransition.Pending(
                prepared.Parsed,
                prepared.PendingDecision
                    ?? throw new InvalidDataException(
                        "Staged match has no result."))
            : CatalogTransition.ForMetadata(
                prepared.Metadata,
                prepared.LocalPosterPath,
                prepared.LocalBackdropPath);
    }

    private static string ValidateLocalMetadata(string title, int? year)
    {
        var normalized = title.Trim();
        if (normalized.Length is < 1 or > 200)
        {
            throw new ArgumentException(
                "Movie title must be between 1 and 200 characters.",
                nameof(title));
        }

        if (year is not null && year is < 1800 or > 2200)
        {
            throw new ArgumentOutOfRangeException(nameof(year));
        }

        return normalized;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private sealed record CatalogTransition(
        CatalogTransitionKind Kind,
        ParsedMovieFileName? Parsed = null,
        MovieMatchDecision? Decision = null,
        MovieMetadata? Metadata = null,
        string? LocalPosterPath = null,
        string? LocalBackdropPath = null,
        string? LocalTitle = null,
        int? LocalYear = null)
    {
        public static CatalogTransition Pending(
            ParsedMovieFileName parsed,
            MovieMatchDecision decision) =>
            new(CatalogTransitionKind.Pending, parsed, decision);

        public static CatalogTransition ForMetadata(
            MovieMetadata metadata,
            string? localPosterPath,
            string? localBackdropPath) =>
            new(
                CatalogTransitionKind.Metadata,
                Metadata: metadata,
                LocalPosterPath: localPosterPath,
                LocalBackdropPath: localBackdropPath);

        public static CatalogTransition Local(string title, int? year) =>
            new(
                CatalogTransitionKind.Local,
                LocalTitle: title,
                LocalYear: year);
    }

    private enum CatalogTransitionKind
    {
        Pending,
        Metadata,
        Local
    }

    private sealed class ObservationParameters
    {
        private readonly DbParameter _runId;
        private readonly DbParameter _normalizedPath;
        private readonly DbParameter _relativePath;
        private readonly DbParameter _length;
        private readonly DbParameter _lastModifiedAt;
        private readonly DbParameter _durationSeconds;
        private readonly DbParameter _container;
        private readonly DbParameter _videoCodec;
        private readonly DbParameter _audioCodec;
        private readonly DbParameter _width;
        private readonly DbParameter _height;
        private readonly DbParameter _audioChannels;
        private readonly DbParameter _probeError;
        private readonly DbParameter _assignedMediaFileId;
        private readonly DbParameter _matchResolutionJson;

        public ObservationParameters(DbCommand command)
        {
            _runId = AddParameter(command, "@RunId");
            _normalizedPath = AddParameter(command, "@NormalizedPath");
            _relativePath = AddParameter(command, "@RelativePath");
            _length = AddParameter(command, "@Length");
            _lastModifiedAt = AddParameter(command, "@LastModifiedAt");
            _durationSeconds = AddParameter(command, "@DurationSeconds");
            _container = AddParameter(command, "@Container");
            _videoCodec = AddParameter(command, "@VideoCodec");
            _audioCodec = AddParameter(command, "@AudioCodec");
            _width = AddParameter(command, "@Width");
            _height = AddParameter(command, "@Height");
            _audioChannels = AddParameter(command, "@AudioChannels");
            _probeError = AddParameter(command, "@ProbeError");
            _assignedMediaFileId = AddParameter(
                command,
                "@AssignedMediaFileId");
            _matchResolutionJson = AddParameter(
                command,
                "@MatchResolutionJson");
        }

        public void Set(Guid runId, StagedCatalogObservation observation)
        {
            SetValue(_runId, runId.ToString("N"));
            SetValue(_normalizedPath, observation.NormalizedPath);
            SetValue(_relativePath, observation.RelativePath);
            SetValue(_length, observation.Length);
            SetValue(_lastModifiedAt, FormatDate(observation.LastModified));
            SetValue(_durationSeconds, observation.Probe?.Duration.TotalSeconds);
            SetValue(_container, observation.Probe?.Container);
            SetValue(_videoCodec, observation.Probe?.VideoCodec);
            SetValue(_audioCodec, observation.Probe?.AudioCodec);
            SetValue(_width, observation.Probe?.Width);
            SetValue(_height, observation.Probe?.Height);
            SetValue(_audioChannels, observation.Probe?.AudioChannels);
            SetValue(_probeError, observation.ProbeError);
            SetValue(
                _assignedMediaFileId,
                observation.AssignedMediaFileId.ToString("N"));
            SetValue(_matchResolutionJson, observation.MatchResolutionJson);
        }

        private static DbParameter AddParameter(
            DbCommand command,
            string name)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
            return parameter;
        }

        private static void SetValue(DbParameter parameter, object? value)
        {
            parameter.Value = value ?? DBNull.Value;
        }
    }

    private sealed class MediaFileRow
    {
        public string Id { get; init; } = string.Empty;
        public long Length
        {
            get; init;
        }
        public string LastModifiedAt { get; init; } = string.Empty;
        public long IsAvailable
        {
            get; init;
        }
        public double? DurationSeconds
        {
            get; init;
        }
        public string? Container
        {
            get; init;
        }
        public string? VideoCodec
        {
            get; init;
        }
        public long IsAssociated
        {
            get; init;
        }
    }

    private sealed class ScanRunRow
    {
        public string Id { get; init; } = string.Empty;
        public string LibrarySourceId { get; init; } = string.Empty;
        public string RootPath { get; init; } = string.Empty;
        public string StartedAt { get; init; } = string.Empty;
        public string? CompletedAt
        {
            get; init;
        }
        public long Succeeded
        {
            get; init;
        }
        public long DiscoveredFiles
        {
            get; init;
        }
        public long ChangedFiles
        {
            get; init;
        }
        public long MissingFiles
        {
            get; init;
        }
        public string? Error
        {
            get; init;
        }
    }

    private sealed class PendingRow
    {
        public string MediaFileId { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string ParsedTitle { get; init; } = string.Empty;
        public long? ParsedYear
        {
            get; init;
        }
        public long Outcome
        {
            get; init;
        }
        public string Explanation { get; init; } = string.Empty;
        public string CandidatesJson { get; init; } = "[]";
        public string UpdatedAt { get; init; } = string.Empty;
    }

    private sealed class StagedTransitionRow
    {
        public string MediaFileId { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string? ProbeError
        {
            get; init;
        }
        public string? MatchResolutionJson
        {
            get; init;
        }
    }
}
