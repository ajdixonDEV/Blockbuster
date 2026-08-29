using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Blockbuster.Core.Media;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Persistence;
using Dapper;

namespace Blockbuster.Infrastructure.Movies;

public sealed class MovieCatalogStore(IDbConnectionFactory connections) : IMovieCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MovieScanFile?> FindFileAsync(
        string librarySourceId,
        string rootPath,
        string normalizedRelativePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MediaFileRow>(new CommandDefinition("""
            SELECT id, library_source_id LibrarySourceId, root_path RootPath,
              normalized_relative_path NormalizedRelativePath, length,
              last_modified_at LastModifiedAt, is_available IsAvailable, probe_error ProbeError,
              EXISTS(SELECT 1 FROM movie_versions v WHERE v.media_file_id=media_files.id) IsAssociated
            FROM media_files
            WHERE library_source_id=@LibrarySourceId AND root_path=@RootPath
              AND normalized_relative_path=@NormalizedRelativePath
            """, new { LibrarySourceId = librarySourceId, RootPath = rootPath, NormalizedRelativePath = normalizedRelativePath },
            cancellationToken: cancellationToken));
        return row is null ? null : ToScanFile(row);
    }

    public async Task<MovieScanFile> UpsertFileAsync(MediaFileUpsert file, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO media_files(
              id,library_source_id,root_path,media_kind,relative_path,normalized_relative_path,
              length,last_modified_at,duration_seconds,container,video_codec,audio_codec,width,height,
              audio_channels,probe_error,is_available,first_seen_at,last_seen_at)
            VALUES(
              @Id,@LibrarySourceId,@RootPath,@MediaKind,@RelativePath,@NormalizedRelativePath,
              @Length,@LastModifiedAt,@DurationSeconds,@Container,@VideoCodec,@AudioCodec,@Width,@Height,
              @AudioChannels,@ProbeError,1,@Now,@Now)
            ON CONFLICT(library_source_id,root_path,normalized_relative_path) DO UPDATE SET
              relative_path=excluded.relative_path,length=excluded.length,last_modified_at=excluded.last_modified_at,
              duration_seconds=excluded.duration_seconds,container=excluded.container,video_codec=excluded.video_codec,
              audio_codec=excluded.audio_codec,width=excluded.width,height=excluded.height,
              audio_channels=excluded.audio_channels,probe_error=excluded.probe_error,is_available=1,last_seen_at=excluded.last_seen_at
            """, new
            {
                Id = id.ToString("N"),
                file.LibrarySourceId,
                file.RootPath,
                MediaKind = (int)MediaKind.Movie,
                file.RelativePath,
                file.NormalizedRelativePath,
                file.Length,
                LastModifiedAt = file.LastModified.ToString("O", CultureInfo.InvariantCulture),
                DurationSeconds = file.Probe?.Duration.TotalSeconds,
                file.Probe?.Container,
                file.Probe?.VideoCodec,
                file.Probe?.AudioCodec,
                file.Probe?.Width,
                file.Probe?.Height,
                file.Probe?.AudioChannels,
                file.ProbeError,
                Now = now
            }, cancellationToken: cancellationToken));

        return (await FindFileAsync(file.LibrarySourceId, file.RootPath, file.NormalizedRelativePath, cancellationToken))!;
    }

    public async Task<int> MarkMissingAsync(
        string librarySourceId,
        string rootPath,
        IReadOnlyCollection<string> seenNormalizedPaths,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var command = seenNormalizedPaths.Count == 0
            ? new CommandDefinition("""
                UPDATE media_files SET is_available=0
                WHERE library_source_id=@LibrarySourceId AND root_path=@RootPath AND is_available=1
                """, new { LibrarySourceId = librarySourceId, RootPath = rootPath }, cancellationToken: cancellationToken)
            : new CommandDefinition("""
                UPDATE media_files SET is_available=0
                WHERE library_source_id=@LibrarySourceId AND root_path=@RootPath AND is_available=1
                  AND normalized_relative_path NOT IN @Seen
                """, new { LibrarySourceId = librarySourceId, RootPath = rootPath, Seen = seenNormalizedPaths }, cancellationToken: cancellationToken);
        return await connection.ExecuteAsync(command);
    }

    public async Task<Guid> StartScanRunAsync(
        string librarySourceId,
        string rootPath,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var started = startedAt.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO library_scan_runs(id,library_source_id,root_path,started_at)
            VALUES(@Id,@LibrarySourceId,@RootPath,@StartedAt);
            """, new { Id = id.ToString("N"), LibrarySourceId = librarySourceId, RootPath = rootPath, StartedAt = started }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO configured_library_scan_state(library_source_id,root_path,last_started_at)
            VALUES(@LibrarySourceId,@RootPath,@StartedAt)
            ON CONFLICT(library_source_id,root_path) DO UPDATE SET last_started_at=excluded.last_started_at
            """, new { LibrarySourceId = librarySourceId, RootPath = rootPath, StartedAt = started }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task CompleteScanRunAsync(
        Guid runId,
        bool succeeded,
        int discoveredFiles,
        int changedFiles,
        int missingFiles,
        string? failureMessage,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE library_scan_runs SET completed_at=@Now,succeeded=@Succeeded,
              discovered_files=@DiscoveredFiles,changed_files=@ChangedFiles,missing_files=@MissingFiles,error=@Error
            WHERE id=@Id
            """, new { Id = runId.ToString("N"), Now = now, Succeeded = succeeded, DiscoveredFiles = discoveredFiles, ChangedFiles = changedFiles, MissingFiles = missingFiles, Error = failureMessage }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE configured_library_scan_state SET last_completed_at=@Now,last_succeeded=@Succeeded,last_error=@Error
            WHERE (library_source_id,root_path)=(SELECT library_source_id,root_path FROM library_scan_runs WHERE id=@Id)
            """, new { Id = runId.ToString("N"), Now = now, Succeeded = succeeded, Error = failureMessage }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryScanRun>> ListScanRunsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ScanRunRow>(new CommandDefinition("""
            SELECT id,library_source_id LibrarySourceId,root_path RootPath,started_at StartedAt,
              completed_at CompletedAt,succeeded Succeeded,discovered_files DiscoveredFiles,
              changed_files ChangedFiles,missing_files MissingFiles,error
            FROM library_scan_runs ORDER BY started_at DESC LIMIT @Limit
            """, new { Limit = Math.Clamp(limit, 1, 100) }, cancellationToken: cancellationToken));
        return rows.Select(row => new LibraryScanRun(
            Guid.ParseExact(row.Id, "N"), row.LibrarySourceId, row.RootPath, ParseDate(row.StartedAt),
            row.CompletedAt is null ? null : ParseDate(row.CompletedAt), row.Succeeded != 0,
            checked((int)row.DiscoveredFiles), checked((int)row.ChangedFiles), checked((int)row.MissingFiles), row.Error)).ToList();
    }

    public async Task QueuePendingMatchAsync(Guid mediaFileId, ParsedMovieFileName parsed, MovieMatchDecision decision, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pending_movie_matches(media_file_id,parsed_title,parsed_year,outcome,explanation,candidates_json,updated_at)
            VALUES(@MediaFileId,@Title,@Year,@Outcome,@Explanation,@Candidates,@Now)
            ON CONFLICT(media_file_id) DO UPDATE SET parsed_title=excluded.parsed_title,parsed_year=excluded.parsed_year,
              outcome=excluded.outcome,explanation=excluded.explanation,candidates_json=excluded.candidates_json,updated_at=excluded.updated_at
            """, new
            {
                MediaFileId = mediaFileId.ToString("N"),
                parsed.Title,
                parsed.Year,
                Outcome = (int)decision.Outcome,
                decision.Explanation,
                Candidates = JsonSerializer.Serialize(decision.Candidates, JsonOptions),
                Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<PendingMovieMatch>> ListPendingMatchesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PendingRow>(new CommandDefinition("""
            SELECT p.media_file_id MediaFileId,f.relative_path RelativePath,p.parsed_title ParsedTitle,
              p.parsed_year ParsedYear,p.outcome Outcome,p.explanation Explanation,
              p.candidates_json CandidatesJson,p.updated_at UpdatedAt
            FROM pending_movie_matches p JOIN media_files f ON f.id=p.media_file_id
            ORDER BY p.updated_at DESC
            """, cancellationToken: cancellationToken));
        return rows.Select(row => new PendingMovieMatch(
            Guid.ParseExact(row.MediaFileId, "N"), row.RelativePath, row.ParsedTitle, row.ParsedYear is null ? null : checked((int)row.ParsedYear.Value),
            (MovieMatchOutcome)checked((int)row.Outcome), row.Explanation,
            JsonSerializer.Deserialize<List<MovieMetadataCandidate>>(row.CandidatesJson, JsonOptions) ?? [], ParseDate(row.UpdatedAt))).ToList();
    }

    public async Task ApplyMetadataAsync(
        Guid mediaFileId,
        MovieMetadata metadata,
        string? localPosterPath,
        string? localBackdropPath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var proposedMovieId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO movies(id,tmdb_id,provider_title,original_title,provider_year,overview,runtime_seconds,
              poster_provider_path,backdrop_provider_path,local_poster_path,local_backdrop_path,created_at,updated_at)
            VALUES(@MovieId,@TmdbId,@Title,@OriginalTitle,@Year,@Overview,@RuntimeSeconds,
              @PosterPath,@BackdropPath,@LocalPosterPath,@LocalBackdropPath,@Now,@Now)
            ON CONFLICT(tmdb_id) WHERE tmdb_id IS NOT NULL DO UPDATE SET provider_title=excluded.provider_title,original_title=excluded.original_title,
              provider_year=excluded.provider_year,overview=excluded.overview,runtime_seconds=excluded.runtime_seconds,
              poster_provider_path=excluded.poster_provider_path,backdrop_provider_path=excluded.backdrop_provider_path,
              local_poster_path=COALESCE(excluded.local_poster_path,movies.local_poster_path),
              local_backdrop_path=COALESCE(excluded.local_backdrop_path,movies.local_backdrop_path),updated_at=excluded.updated_at
            """, new
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
            }, transaction, cancellationToken: cancellationToken));
        var movieId = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT id FROM movies WHERE tmdb_id=@TmdbId", new { metadata.TmdbId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM movie_genres WHERE movie_id=@MovieId", new { MovieId = movieId }, transaction, cancellationToken: cancellationToken));
        foreach (var genre in metadata.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO movie_genres(movie_id,genre) VALUES(@MovieId,@Genre)", new { MovieId = movieId, Genre = genre }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM movie_versions WHERE media_file_id=@MediaFileId", new { MediaFileId = mediaFileId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO movie_versions(movie_id,media_file_id) VALUES(@MovieId,@MediaFileId)", new { MovieId = movieId, MediaFileId = mediaFileId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM pending_movie_matches WHERE media_file_id=@MediaFileId", new { MediaFileId = mediaFileId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ApplyLocalOverrideAsync(Guid mediaFileId, string title, int? year, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 200) throw new ArgumentException("Movie title must be between 1 and 200 characters.", nameof(title));
        if (year is not null && year is < 1800 or > 2200) throw new ArgumentOutOfRangeException(nameof(year));

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var movieId = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT movie_id FROM movie_versions WHERE media_file_id=@MediaFileId",
            new { MediaFileId = mediaFileId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        if (movieId is null)
        {
            movieId = Guid.NewGuid().ToString("N");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO movies(id,provider_title,provider_year,created_at,updated_at) VALUES(@MovieId,@Title,@Year,@Now,@Now);
                INSERT INTO movie_versions(movie_id,media_file_id) VALUES(@MovieId,@MediaFileId)
                """, new { MovieId = movieId, Title = title, Year = year, Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), MediaFileId = mediaFileId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO movie_overrides(movie_id,title,year,updated_at) VALUES(@MovieId,@Title,@Year,@Now)
            ON CONFLICT(movie_id) DO UPDATE SET title=excluded.title,year=excluded.year,updated_at=excluded.updated_at
            """, new { MovieId = movieId, Title = title, Year = year, Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM pending_movie_matches WHERE media_file_id=@MediaFileId", new { MediaFileId = mediaFileId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearPendingMatchAsync(Guid mediaFileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM pending_movie_matches WHERE media_file_id=@Id", new { Id = mediaFileId.ToString("N") }, cancellationToken: cancellationToken));
    }

    private static MovieScanFile ToScanFile(MediaFileRow row) => new(
        Guid.ParseExact(row.Id, "N"), row.LibrarySourceId, row.RootPath, row.NormalizedRelativePath,
        row.Length, ParseDate(row.LastModifiedAt), row.IsAvailable != 0, row.ProbeError, row.IsAssociated != 0);

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class MediaFileRow
    {
        public string Id { get; init; } = string.Empty;
        public string LibrarySourceId { get; init; } = string.Empty;
        public string RootPath { get; init; } = string.Empty;
        public string NormalizedRelativePath { get; init; } = string.Empty;
        public long Length { get; init; }
        public string LastModifiedAt { get; init; } = string.Empty;
        public long IsAvailable { get; init; }
        public string? ProbeError { get; init; }
        public long IsAssociated { get; init; }
    }

    private sealed class ScanRunRow
    {
        public string Id { get; init; } = string.Empty;
        public string LibrarySourceId { get; init; } = string.Empty;
        public string RootPath { get; init; } = string.Empty;
        public string StartedAt { get; init; } = string.Empty;
        public string? CompletedAt { get; init; }
        public long Succeeded { get; init; }
        public long DiscoveredFiles { get; init; }
        public long ChangedFiles { get; init; }
        public long MissingFiles { get; init; }
        public string? Error { get; init; }
    }

    private sealed class PendingRow
    {
        public string MediaFileId { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string ParsedTitle { get; init; } = string.Empty;
        public long? ParsedYear { get; init; }
        public long Outcome { get; init; }
        public string Explanation { get; init; } = string.Empty;
        public string CandidatesJson { get; init; } = "[]";
        public string UpdatedAt { get; init; } = string.Empty;
    }
}
