using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Blockbuster.Core.Media;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Persistence;
using Dapper;

namespace Blockbuster.Infrastructure.Movies;

public sealed class MovieCatalogStore(IDbConnectionFactory connections) : IMovieCatalogStore, IMovieCatalogReader
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
              duration_seconds DurationSeconds,container Container,video_codec VideoCodec,
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

    public async Task<(int MissingFiles, IReadOnlyList<StagedScanPromotion> Files)> PromoteStagedRunAsync(
        Guid runId, string librarySourceId, string rootPath, int discoveredFiles, int changedFiles,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var observations = (await connection.QueryAsync<ObservationRow>(new CommandDefinition("""
            SELECT normalized_relative_path NormalizedPath,relative_path RelativePath,length,last_modified_at LastModifiedAt,
              duration_seconds DurationSeconds,container,video_codec VideoCodec,audio_codec AudioCodec,width,height,
              audio_channels AudioChannels,probe_error ProbeError,assigned_media_file_id AssignedMediaFileId,
              match_resolution_json MatchResolutionJson
            FROM library_scan_observations WHERE run_id=@RunId ORDER BY normalized_relative_path
            """, new { RunId = runId.ToString("N") }, transaction, cancellationToken: cancellationToken))).ToList();
        var promotions = new List<StagedScanPromotion>(observations.Count);
        foreach (var item in observations)
        {
            var existing = await connection.QuerySingleOrDefaultAsync<MediaFileRow>(new CommandDefinition("""
                SELECT id,library_source_id LibrarySourceId,root_path RootPath,normalized_relative_path NormalizedRelativePath,
                  length,last_modified_at LastModifiedAt,is_available IsAvailable,probe_error ProbeError,
                  duration_seconds DurationSeconds,container Container,video_codec VideoCodec,
                  EXISTS(SELECT 1 FROM movie_versions v WHERE v.media_file_id=media_files.id) IsAssociated
                FROM media_files WHERE library_source_id=@LibrarySourceId AND root_path=@RootPath AND normalized_relative_path=@NormalizedPath
                """, new { LibrarySourceId = librarySourceId, RootPath = rootPath, item.NormalizedPath }, transaction, cancellationToken: cancellationToken));
            var changed = existing is null || existing.IsAvailable == 0 || existing.Length != item.Length
                || !string.Equals(existing.LastModifiedAt, item.LastModifiedAt, StringComparison.Ordinal);
            // An unchanged observation contains no probe values.  A staged probe
            // result (or error) is therefore the explicit signal to replace facts
            // while repairing an otherwise unchanged catalog row.
            var replaceProbeFacts = changed || item.DurationSeconds is not null || item.ProbeError is not null;
            var mediaFileId = existing?.Id ?? item.AssignedMediaFileId ?? Guid.NewGuid().ToString("N");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO media_files(id,library_source_id,root_path,media_kind,relative_path,normalized_relative_path,length,last_modified_at,duration_seconds,container,video_codec,audio_codec,width,height,audio_channels,probe_error,is_available,first_seen_at,last_seen_at)
                VALUES(@Id,@LibrarySourceId,@RootPath,@MediaKind,@RelativePath,@NormalizedPath,@Length,@LastModifiedAt,@DurationSeconds,@Container,@VideoCodec,@AudioCodec,@Width,@Height,@AudioChannels,@ProbeError,1,@Now,@Now)
                ON CONFLICT(library_source_id,root_path,normalized_relative_path) DO UPDATE SET
                  relative_path=excluded.relative_path,length=excluded.length,last_modified_at=excluded.last_modified_at,
                  duration_seconds=CASE WHEN @ReplaceProbeFacts THEN excluded.duration_seconds ELSE media_files.duration_seconds END,
                  container=CASE WHEN @ReplaceProbeFacts THEN excluded.container ELSE media_files.container END,
                  video_codec=CASE WHEN @ReplaceProbeFacts THEN excluded.video_codec ELSE media_files.video_codec END,
                  audio_codec=CASE WHEN @ReplaceProbeFacts THEN excluded.audio_codec ELSE media_files.audio_codec END,
                  width=CASE WHEN @ReplaceProbeFacts THEN excluded.width ELSE media_files.width END,
                  height=CASE WHEN @ReplaceProbeFacts THEN excluded.height ELSE media_files.height END,
                  audio_channels=CASE WHEN @ReplaceProbeFacts THEN excluded.audio_channels ELSE media_files.audio_channels END,
                  probe_error=CASE WHEN @ReplaceProbeFacts THEN excluded.probe_error ELSE media_files.probe_error END,
                  is_available=1,last_seen_at=excluded.last_seen_at
                """, new { Id = mediaFileId, LibrarySourceId = librarySourceId, RootPath = rootPath, MediaKind = (int)MediaKind.Movie, item.RelativePath, item.NormalizedPath, item.Length, item.LastModifiedAt, item.DurationSeconds, item.Container, item.VideoCodec, item.AudioCodec, item.Width, item.Height, item.AudioChannels, item.ProbeError, ReplaceProbeFacts = replaceProbeFacts, Now = now }, transaction, cancellationToken: cancellationToken));
            if (changed)
                await ApplyStagedResolutionAsync(connection, transaction, Guid.ParseExact(mediaFileId, "N"), item.RelativePath, item.ProbeError, item.MatchResolutionJson, now, cancellationToken);
            promotions.Add(new(Guid.ParseExact(mediaFileId, "N"), item.RelativePath, changed, existing is not null && existing.IsAssociated != 0, item.ProbeError));
        }
        var missing = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE media_files SET is_available=0 WHERE library_source_id=@LibrarySourceId AND root_path=@RootPath AND is_available=1
              AND normalized_relative_path NOT IN (SELECT normalized_relative_path FROM library_scan_observations WHERE run_id=@RunId)
            """, new { LibrarySourceId = librarySourceId, RootPath = rootPath, RunId = runId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE library_scan_runs SET completed_at=@Now,succeeded=1,discovered_files=@DiscoveredFiles,changed_files=@ChangedFiles,missing_files=@MissingFiles,error=NULL WHERE id=@RunId;
            UPDATE configured_library_scan_state SET last_completed_at=@Now,last_succeeded=1,last_error=NULL WHERE (library_source_id,root_path)=(SELECT library_source_id,root_path FROM library_scan_runs WHERE id=@RunId);
            DELETE FROM library_scan_observations WHERE run_id=@RunId;
            """, new { RunId = runId.ToString("N"), Now = now, DiscoveredFiles = discoveredFiles, ChangedFiles = changedFiles, MissingFiles = missing }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return (missing, promotions);
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
        row.Length, ParseDate(row.LastModifiedAt), row.IsAvailable != 0, row.ProbeError, row.IsAssociated != 0,
        row.DurationSeconds is not null && !string.IsNullOrWhiteSpace(row.Container) && !string.IsNullOrWhiteSpace(row.VideoCodec));

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async Task ApplyStagedResolutionAsync(DbConnection connection, DbTransaction transaction, Guid mediaFileId, string relativePath, string? probeError, string? json, string now, CancellationToken cancellationToken)
    {
        var parsed = MovieFilenameParser.Parse(relativePath);
        if (probeError is not null)
        {
            await UpsertPendingAsync(connection, transaction, mediaFileId, parsed, new(MovieMatchOutcome.ProbeFailed, null, [], "ffprobe could not read this file; inspect the probe error and retry."), now, cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(json)) return;
        var resolution = JsonSerializer.Deserialize<ScanMatchResolution>(json, JsonOptions) ?? throw new InvalidDataException("Invalid staged movie-match resolution.");
        if (resolution.Metadata is null)
        {
            await UpsertPendingAsync(connection, transaction, mediaFileId, resolution.Parsed, resolution.PendingDecision ?? throw new InvalidDataException("Staged match has no result."), now, cancellationToken);
            return;
        }
        var metadata = resolution.Metadata;
        var proposedMovieId = Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO movies(id,tmdb_id,provider_title,original_title,provider_year,overview,runtime_seconds,poster_provider_path,backdrop_provider_path,local_poster_path,local_backdrop_path,created_at,updated_at)
            VALUES(@MovieId,@TmdbId,@Title,@OriginalTitle,@Year,@Overview,@RuntimeSeconds,@PosterPath,@BackdropPath,@LocalPosterPath,@LocalBackdropPath,@Now,@Now)
            ON CONFLICT(tmdb_id) WHERE tmdb_id IS NOT NULL DO UPDATE SET provider_title=excluded.provider_title,original_title=excluded.original_title,provider_year=excluded.provider_year,overview=excluded.overview,runtime_seconds=excluded.runtime_seconds,poster_provider_path=excluded.poster_provider_path,backdrop_provider_path=excluded.backdrop_provider_path,local_poster_path=COALESCE(excluded.local_poster_path,movies.local_poster_path),local_backdrop_path=COALESCE(excluded.local_backdrop_path,movies.local_backdrop_path),updated_at=excluded.updated_at
            """, new { MovieId = proposedMovieId, metadata.TmdbId, metadata.Title, metadata.OriginalTitle, metadata.Year, metadata.Overview, RuntimeSeconds = metadata.Runtime?.TotalSeconds, metadata.PosterPath, metadata.BackdropPath, LocalPosterPath = resolution.LocalPosterPath, LocalBackdropPath = resolution.LocalBackdropPath, Now = now }, transaction, cancellationToken: cancellationToken));
        var movieId = await connection.QuerySingleAsync<string>(new CommandDefinition("SELECT id FROM movies WHERE tmdb_id=@TmdbId", new { metadata.TmdbId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM movie_genres WHERE movie_id=@MovieId; DELETE FROM movie_versions WHERE media_file_id=@MediaFileId; INSERT INTO movie_versions(movie_id,media_file_id) VALUES(@MovieId,@MediaFileId); DELETE FROM pending_movie_matches WHERE media_file_id=@MediaFileId", new { MovieId = movieId, MediaFileId = mediaFileId.ToString("N") }, transaction, cancellationToken: cancellationToken));
        foreach (var genre in metadata.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO movie_genres(movie_id,genre) VALUES(@MovieId,@Genre)", new { MovieId = movieId, Genre = genre }, transaction, cancellationToken: cancellationToken));
    }

    private static Task<int> UpsertPendingAsync(DbConnection connection, DbTransaction transaction, Guid mediaFileId, ParsedMovieFileName parsed, MovieMatchDecision decision, string now, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pending_movie_matches(media_file_id,parsed_title,parsed_year,outcome,explanation,candidates_json,updated_at)
            VALUES(@MediaFileId,@Title,@Year,@Outcome,@Explanation,@Candidates,@Now)
            ON CONFLICT(media_file_id) DO UPDATE SET parsed_title=excluded.parsed_title,parsed_year=excluded.parsed_year,outcome=excluded.outcome,explanation=excluded.explanation,candidates_json=excluded.candidates_json,updated_at=excluded.updated_at
            """, new { MediaFileId = mediaFileId.ToString("N"), parsed.Title, parsed.Year, Outcome = (int)decision.Outcome, decision.Explanation, Candidates = JsonSerializer.Serialize(decision.Candidates, JsonOptions), Now = now }, transaction, cancellationToken: cancellationToken));

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
        public double? DurationSeconds { get; init; }
        public string? Container { get; init; }
        public string? VideoCodec { get; init; }
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

    private sealed class ObservationRow
    {
        public string NormalizedPath { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public long Length { get; init; }
        public string LastModifiedAt { get; init; } = string.Empty;
        public double? DurationSeconds { get; init; }
        public string? Container { get; init; }
        public string? VideoCodec { get; init; }
        public string? AudioCodec { get; init; }
        public long? Width { get; init; }
        public long? Height { get; init; }
        public long? AudioChannels { get; init; }
        public string? ProbeError { get; init; }
        public string? AssignedMediaFileId { get; init; }
        public string? MatchResolutionJson { get; init; }
    }
}
