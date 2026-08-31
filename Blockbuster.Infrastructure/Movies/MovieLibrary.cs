using System.Globalization;
using Blockbuster.Core.Persistence;
using Blockbuster.Core.Playback;
using Blockbuster.Infrastructure.Configuration;
using Dapper;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Movies;

public sealed class MovieLibrary(
    IDbConnectionFactory connections,
    IOptions<HistoryOptions> history,
    IStoragePathResolver storagePaths) : IMovieLibrary, IPlaybackProgressStore
{
    public async Task<MovieCatalogPage> BrowseAsync(
        Guid profileId,
        MovieCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 60);
        var search = string.IsNullOrWhiteSpace(query.Search)
            ? null
            : $"%{query.Search.Trim()}%";
        var order = query.Sort switch
        {
            MovieSort.YearDescending =>
                "COALESCE(movie_override.year, movie.provider_year) DESC, "
                + "COALESCE(movie_override.title, movie.provider_title) "
                + "COLLATE NOCASE",
            MovieSort.RecentlyAdded => "movie.created_at DESC",
            _ =>
                "COALESCE(movie_override.title, movie.provider_title) "
                + "COLLATE NOCASE"
        };
        var where =
            """
            WHERE (
                @Search IS NULL
                OR COALESCE(movie_override.title, movie.provider_title)
                   LIKE @Search ESCAPE '\'
            )
              AND (
                @Genre IS NULL
                OR EXISTS(
                    SELECT 1
                    FROM movie_genres AS filtered_genre
                    WHERE filtered_genre.movie_id = movie.id
                      AND filtered_genre.genre = @Genre COLLATE NOCASE
                )
              )
              AND (
                @Year IS NULL
                OR COALESCE(movie_override.year, movie.provider_year) = @Year
              )
              AND EXISTS(
                SELECT 1
                FROM movie_versions AS visible_version
                WHERE visible_version.movie_id = movie.id
              )
            """;
        var arguments = new
        {
            ProfileId = profileId.ToString("N"),
            Search = search,
            Genre = NullIfBlank(query.Genre),
            query.Year,
            Limit = pageSize,
            Offset = (page - 1) * pageSize
        };

        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var total = await connection.QuerySingleAsync<int>(
            new CommandDefinition(
                $"""
                SELECT COUNT(*)
                FROM movies AS movie
                LEFT JOIN movie_overrides AS movie_override
                  ON movie_override.movie_id = movie.id
                {where}
                """,
                arguments,
                cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<CatalogRow>(
            new CommandDefinition(
                $"""
                SELECT
                    movie.id AS Id,
                    COALESCE(movie_override.title, movie.provider_title) AS Title,
                    COALESCE(movie_override.year, movie.provider_year) AS Year,
                    movie.overview AS Overview,
                    movie.local_poster_path AS PosterPath,
                    movie.created_at AS AddedAt,
                    (
                        SELECT COUNT(*)
                        FROM movie_versions AS version
                        JOIN media_files AS file
                          ON file.id = version.media_file_id
                        WHERE version.movie_id = movie.id
                          AND file.is_available = 1
                    ) AS AvailableVersions,
                    progress.position_seconds AS ProgressSeconds,
                    movie.runtime_seconds AS DurationSeconds,
                    COALESCE((
                        SELECT group_concat(genre, char(31))
                        FROM (
                            SELECT genre
                            FROM movie_genres AS genre
                            WHERE genre.movie_id = movie.id
                            ORDER BY genre
                        )
                    ), '') AS Genres
                FROM movies AS movie
                LEFT JOIN movie_overrides AS movie_override
                  ON movie_override.movie_id = movie.id
                LEFT JOIN movie_progress AS progress
                  ON progress.movie_id = movie.id
                 AND progress.profile_id = @ProfileId
                {where}
                ORDER BY {order}
                LIMIT @Limit
                OFFSET @Offset
                """,
                arguments,
                cancellationToken: cancellationToken));
        var genres = (await connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT DISTINCT genre.genre
                FROM movie_genres AS genre
                WHERE EXISTS(
                    SELECT 1
                    FROM movie_versions AS version
                    WHERE version.movie_id = genre.movie_id
                )
                ORDER BY genre.genre COLLATE NOCASE
                """,
                cancellationToken: cancellationToken))).ToList();
        var years = (await connection.QueryAsync<int>(
            new CommandDefinition(
                """
                SELECT DISTINCT COALESCE(
                    movie_override.year,
                    movie.provider_year
                )
                FROM movies AS movie
                LEFT JOIN movie_overrides AS movie_override
                  ON movie_override.movie_id = movie.id
                WHERE COALESCE(
                    movie_override.year,
                    movie.provider_year
                ) IS NOT NULL
                  AND EXISTS(
                    SELECT 1
                    FROM movie_versions AS version
                    WHERE version.movie_id = movie.id
                  )
                ORDER BY 1 DESC
                """,
                cancellationToken: cancellationToken))).ToList();

        return new MovieCatalogPage(
            rows.Select(ToItem).ToList(),
            total,
            page,
            pageSize,
            genres,
            years);
    }

    public async Task<MovieDetails?> GetAsync(
        Guid movieId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<DetailsRow>(
            new CommandDefinition(
                """
                SELECT
                    movie.id AS Id,
                    COALESCE(movie_override.title, movie.provider_title) AS Title,
                    COALESCE(movie_override.year, movie.provider_year) AS Year,
                    movie.original_title AS OriginalTitle,
                    movie.overview AS Overview,
                    movie.local_poster_path AS PosterPath,
                    movie.local_backdrop_path AS BackdropPath,
                    progress.position_seconds AS ProgressSeconds,
                    COALESCE(progress.revision, 0) AS ProgressRevision,
                    COALESCE((
                        SELECT group_concat(genre, char(31))
                        FROM (
                            SELECT genre
                            FROM movie_genres AS genre
                            WHERE genre.movie_id = movie.id
                            ORDER BY genre
                        )
                    ), '') AS Genres
                FROM movies AS movie
                LEFT JOIN movie_overrides AS movie_override
                  ON movie_override.movie_id = movie.id
                LEFT JOIN movie_progress AS progress
                  ON progress.movie_id = movie.id
                 AND progress.profile_id = @ProfileId
                WHERE movie.id = @Id
                """,
                new
                {
                    Id = movieId.ToString("N"),
                    ProfileId = profileId.ToString("N")
                },
                cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var versions = await connection.QueryAsync<VersionRow>(
            new CommandDefinition(
                """
                SELECT
                    file.id AS MediaFileId,
                    file.relative_path AS RelativePath,
                    file.container AS Container,
                    file.video_codec AS VideoCodec,
                    file.audio_codec AS AudioCodec,
                    file.width AS Width,
                    file.height AS Height,
                    file.audio_channels AS AudioChannels,
                    file.duration_seconds AS DurationSeconds,
                    file.length AS Length,
                    file.last_modified_at AS LastModifiedAt,
                    file.is_available AS IsAvailable
                FROM movie_versions AS version
                JOIN media_files AS file
                  ON file.id = version.media_file_id
                WHERE version.movie_id = @Id
                ORDER BY
                    file.is_available DESC,
                    COALESCE(file.height, 0) DESC,
                    file.relative_path COLLATE NOCASE
                """,
                new
                {
                    Id = movieId.ToString("N")
                },
                cancellationToken: cancellationToken));
        return new MovieDetails(
            Guid.ParseExact(row.Id, "N"),
            row.Title,
            ToInt(row.Year),
            row.OriginalTitle,
            row.Overview,
            ArtworkUrl(movieId, "poster", row.PosterPath),
            ArtworkUrl(movieId, "backdrop", row.BackdropPath),
            Split(row.Genres),
            versions.Select(ToVersion).ToList(),
            Seconds(row.ProgressSeconds),
            row.ProgressRevision);
    }

    public async Task<MediaStreamSource?> AuthorizeStreamAsync(
        Guid mediaFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<StreamRow>(
            new CommandDefinition(
                """
                SELECT
                    version.movie_id AS MovieId,
                    file.root_path AS RootPath,
                    file.relative_path AS RelativePath,
                    file.container AS Container
                FROM media_files AS file
                JOIN movie_versions AS version
                  ON version.media_file_id = file.id
                WHERE file.id = @Id
                  AND file.is_available = 1
                """,
                new
                {
                    Id = mediaFileId.ToString("N")
                },
                cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var root = Path.GetFullPath(row.RootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, row.RelativePath));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            return null;
        }

        var info = new FileInfo(fullPath);
        return new MediaStreamSource(
            mediaFileId,
            Guid.ParseExact(row.MovieId, "N"),
            fullPath,
            ContentType(row.Container, fullPath),
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc));
    }

    public async Task<ArtworkSource?> GetArtworkAsync(
        Guid movieId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("poster" or "backdrop"))
        {
            return null;
        }

        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var sql = kind == "poster"
            ? "SELECT local_poster_path FROM movies WHERE id = @Id"
            : "SELECT local_backdrop_path FROM movies WHERE id = @Id";
        var path = await connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                sql,
                new
                {
                    Id = movieId.ToString("N")
                },
                cancellationToken: cancellationToken));
        var fullPath = ResolveArtworkPath(path);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return null;
        }

        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        return new ArtworkSource(
            fullPath,
            contentType,
            new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)));
    }

    public async Task<PlaybackProgress?> GetProgressAsync(
        Guid profileId,
        Guid movieId,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ProgressRow>(
            new CommandDefinition(
                """
                SELECT
                    position_seconds AS PositionSeconds,
                    revision,
                    updated_at AS UpdatedAt
                FROM movie_progress
                WHERE profile_id = @ProfileId
                  AND movie_id = @MovieId
                """,
                Keys(profileId, movieId),
                cancellationToken: cancellationToken));
        return row is null
            ? null
            : new PlaybackProgress(
                profileId,
                movieId,
                TimeSpan.FromSeconds(row.PositionSeconds),
                row.Revision,
                Parse(row.UpdatedAt));
    }

    public async Task<PlaybackProgressResult> SaveAsync(
        Guid profileId,
        Guid movieId,
        TimeSpan position,
        long expectedRevision,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        eventType = eventType is "play" or "pause" or "progress" or "ended"
            ? eventType
            : "progress";

        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<ProgressRow>(
            new CommandDefinition(
                """
                SELECT
                    position_seconds AS PositionSeconds,
                    revision,
                    updated_at AS UpdatedAt
                FROM movie_progress
                WHERE profile_id = @ProfileId
                  AND movie_id = @MovieId
                """,
                Keys(profileId, movieId),
                transaction,
                cancellationToken: cancellationToken));
        var accepted = current is null
            ? expectedRevision == 0
            : current.Revision == expectedRevision;
        if (accepted)
        {
            current = await SaveAcceptedProgressAsync(
                connection,
                transaction,
                profileId,
                movieId,
                position,
                expectedRevision + 1,
                eventType,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        current ??= new ProgressRow
        {
            UpdatedAt = DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture)
        };
        return new PlaybackProgressResult(
            accepted,
            new PlaybackProgress(
                profileId,
                movieId,
                TimeSpan.FromSeconds(current.PositionSeconds),
                current.Revision,
                Parse(current.UpdatedAt)));
    }

    public async Task<IReadOnlyList<PlaybackEvent>> RecentActivityAsync(
        Guid profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await connections.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EventRow>(
            new CommandDefinition(
                """
                SELECT
                    event.id AS Id,
                    event.movie_id AS MovieId,
                    COALESCE(movie_override.title, movie.provider_title)
                        AS MovieTitle,
                    event.event_type AS EventType,
                    event.position_seconds AS PositionSeconds,
                    event.occurred_at AS OccurredAt
                FROM playback_events AS event
                JOIN movies AS movie
                  ON movie.id = event.movie_id
                LEFT JOIN movie_overrides AS movie_override
                  ON movie_override.movie_id = movie.id
                WHERE event.profile_id = @ProfileId
                ORDER BY event.occurred_at DESC, event.id DESC
                LIMIT @Limit
                """,
                new
                {
                    ProfileId = profileId.ToString("N"),
                    Limit = Math.Clamp(limit, 1, 100)
                },
                cancellationToken: cancellationToken));
        return rows
            .Select(row => new PlaybackEvent(
                Guid.ParseExact(row.Id, "N"),
                profileId,
                Guid.ParseExact(row.MovieId, "N"),
                row.MovieTitle,
                row.EventType,
                TimeSpan.FromSeconds(row.PositionSeconds),
                Parse(row.OccurredAt)))
            .ToList();
    }

    private string? ResolveArtworkPath(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        var artworkRoot = Path.GetFullPath(storagePaths.ArtworkPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(
            Path.IsPathFullyQualified(storedPath)
                ? storedPath
                : Path.Combine(artworkRoot, storedPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.StartsWith(
            artworkRoot + Path.DirectorySeparatorChar,
            comparison)
            ? fullPath
            : null;
    }

    private async Task<ProgressRow> SaveAcceptedProgressAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid profileId,
        Guid movieId,
        TimeSpan position,
        long revision,
        string eventType,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString(
            "O",
            CultureInfo.InvariantCulture);
        var arguments = new
        {
            ProfileId = profileId.ToString("N"),
            MovieId = movieId.ToString("N"),
            Position = position.TotalSeconds,
            Revision = revision,
            Now = now
        };
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO movie_progress(
                    profile_id,
                    movie_id,
                    position_seconds,
                    revision,
                    updated_at
                )
                VALUES(
                    @ProfileId,
                    @MovieId,
                    @Position,
                    @Revision,
                    @Now
                )
                ON CONFLICT(profile_id, movie_id) DO UPDATE SET
                    position_seconds = excluded.position_seconds,
                    revision = excluded.revision,
                    updated_at = excluded.updated_at
                """,
                arguments,
                transaction,
                cancellationToken: cancellationToken));

        if (eventType != "progress")
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO playback_events(
                        id,
                        profile_id,
                        movie_id,
                        event_type,
                        position_seconds,
                        occurred_at
                    )
                    VALUES(
                        @Id,
                        @ProfileId,
                        @MovieId,
                        @EventType,
                        @Position,
                        @Now
                    )
                    """,
                    new
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        arguments.ProfileId,
                        arguments.MovieId,
                        EventType = eventType,
                        arguments.Position,
                        arguments.Now
                    },
                    transaction,
                    cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM playback_events
                WHERE profile_id = @ProfileId
                  AND id NOT IN (
                    SELECT id
                    FROM playback_events
                    WHERE profile_id = @ProfileId
                    ORDER BY occurred_at DESC, id DESC
                    LIMIT @Limit
                  )
                """,
                new
                {
                    ProfileId = profileId.ToString("N"),
                    Limit = history.Value.MaximumEventsPerProfile
                },
                transaction,
                cancellationToken: cancellationToken));
        return new ProgressRow
        {
            PositionSeconds = position.TotalSeconds,
            Revision = revision,
            UpdatedAt = now
        };
    }

    private static object Keys(Guid profileId, Guid movieId) =>
        new
        {
            ProfileId = profileId.ToString("N"),
            MovieId = movieId.ToString("N")
        };

    private static MovieCatalogItem ToItem(CatalogRow row)
    {
        var id = Guid.ParseExact(row.Id, "N");
        return new MovieCatalogItem(
            id,
            row.Title,
            ToInt(row.Year),
            row.Overview,
            ArtworkUrl(id, "poster", row.PosterPath),
            Split(row.Genres),
            checked((int)row.AvailableVersions),
            Parse(row.AddedAt),
            Seconds(row.ProgressSeconds),
            Seconds(row.DurationSeconds));
    }

    private static MovieVersion ToVersion(VersionRow row)
    {
        var compatible = Compatible(
            row.Container,
            row.VideoCodec,
            row.AudioCodec);
        var explanation = compatible
            ? "Direct play is expected in modern browsers."
            : "This container or codec is not broadly supported by browsers; "
                + "transcoding is not yet available.";
        return new MovieVersion(
            Guid.ParseExact(row.MediaFileId, "N"),
            Quality(row),
            Path.GetFileName(row.RelativePath),
            row.Container ?? "unknown",
            row.VideoCodec,
            row.AudioCodec,
            ToInt(row.Width),
            ToInt(row.Height),
            ToInt(row.AudioChannels),
            Seconds(row.DurationSeconds),
            row.Length,
            Parse(row.LastModifiedAt),
            row.IsAvailable != 0,
            compatible,
            explanation);
    }

    private static bool Compatible(
        string? container,
        string? videoCodec,
        string? audioCodec)
    {
        var compatibleContainer =
            container?.Contains("mp4", StringComparison.OrdinalIgnoreCase) == true
            || container?.Contains("webm", StringComparison.OrdinalIgnoreCase)
                == true
            || container?.Contains("mov", StringComparison.OrdinalIgnoreCase)
                == true;
        var compatibleVideo = videoCodec is "h264" or "vp8" or "vp9" or "av1";
        var compatibleAudio = audioCodec is null
            or "aac"
            or "mp3"
            or "opus"
            or "vorbis";
        return compatibleContainer && compatibleVideo && compatibleAudio;
    }

    private static string Quality(VersionRow row)
    {
        var height = row.Height is null ? "Unknown" : $"{row.Height}p";
        var container = (row.Container ?? "unknown").ToUpperInvariant();
        return $"{height} · {container}";
    }

    private static string ContentType(string? container, string path)
    {
        if (container?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true
            || Path.GetExtension(path).Equals(
                ".webm",
                StringComparison.OrdinalIgnoreCase))
        {
            return "video/webm";
        }

        return Path.GetExtension(path).Equals(
            ".ogg",
            StringComparison.OrdinalIgnoreCase)
            ? "video/ogg"
            : "video/mp4";
    }

    private static string? ArtworkUrl(
        Guid id,
        string kind,
        string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : $"/artwork/{id:N}/{kind}";

    private static string[] Split(string value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(
                (char)31,
                StringSplitOptions.RemoveEmptyEntries);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ToInt(long? value) =>
        value is null ? null : checked((int)value.Value);

    private static TimeSpan? Seconds(double? value) =>
        value is null ? null : TimeSpan.FromSeconds(value.Value);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private sealed class CatalogRow
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public long? Year
        {
            get; init;
        }
        public string? Overview
        {
            get; init;
        }
        public string? PosterPath
        {
            get; init;
        }
        public string AddedAt { get; init; } = string.Empty;
        public long AvailableVersions
        {
            get; init;
        }
        public double? ProgressSeconds
        {
            get; init;
        }
        public double? DurationSeconds
        {
            get; init;
        }
        public string Genres { get; init; } = string.Empty;
    }

    private sealed class DetailsRow
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public long? Year
        {
            get; init;
        }
        public string? OriginalTitle
        {
            get; init;
        }
        public string? Overview
        {
            get; init;
        }
        public string? PosterPath
        {
            get; init;
        }
        public string? BackdropPath
        {
            get; init;
        }
        public double? ProgressSeconds
        {
            get; init;
        }
        public long ProgressRevision
        {
            get; init;
        }
        public string Genres { get; init; } = string.Empty;
    }

    private sealed class VersionRow
    {
        public string MediaFileId { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string? Container
        {
            get; init;
        }
        public string? VideoCodec
        {
            get; init;
        }
        public string? AudioCodec
        {
            get; init;
        }
        public long? Width
        {
            get; init;
        }
        public long? Height
        {
            get; init;
        }
        public long? AudioChannels
        {
            get; init;
        }
        public double? DurationSeconds
        {
            get; init;
        }
        public long Length
        {
            get; init;
        }
        public string LastModifiedAt { get; init; } = string.Empty;
        public long IsAvailable
        {
            get; init;
        }
    }

    private sealed class StreamRow
    {
        public string MovieId { get; init; } = string.Empty;
        public string RootPath { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string? Container
        {
            get; init;
        }
    }

    private sealed class ProgressRow
    {
        public double PositionSeconds
        {
            get; init;
        }
        public long Revision
        {
            get; init;
        }
        public string UpdatedAt { get; init; } = string.Empty;
    }

    private sealed class EventRow
    {
        public string Id { get; init; } = string.Empty;
        public string MovieId { get; init; } = string.Empty;
        public string MovieTitle { get; init; } = string.Empty;
        public string EventType { get; init; } = string.Empty;
        public double PositionSeconds
        {
            get; init;
        }
        public string OccurredAt { get; init; } = string.Empty;
    }
}
