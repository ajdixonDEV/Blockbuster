using System.Globalization;
using Blockbuster.Core.Persistence;
using Blockbuster.Core.Playback;
using Blockbuster.Infrastructure.Configuration;
using Dapper;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Movies;

public sealed class MovieLibrary(IDbConnectionFactory connections, IOptions<HistoryOptions> history,
    IStoragePathResolver storagePaths) : IMovieLibrary, IPlaybackProgressStore
{
    public async Task<MovieCatalogPage> BrowseAsync(Guid profileId, MovieCatalogQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 60);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{query.Search.Trim()}%";
        var order = query.Sort switch
        {
            MovieSort.YearDescending => "COALESCE(o.year,m.provider_year) DESC, COALESCE(o.title,m.provider_title) COLLATE NOCASE",
            MovieSort.RecentlyAdded => "m.created_at DESC",
            _ => "COALESCE(o.title,m.provider_title) COLLATE NOCASE"
        };
        var where = """
            WHERE (@Search IS NULL OR COALESCE(o.title,m.provider_title) LIKE @Search ESCAPE '\')
              AND (@Genre IS NULL OR EXISTS(SELECT 1 FROM movie_genres fg WHERE fg.movie_id=m.id AND fg.genre=@Genre COLLATE NOCASE))
              AND (@Year IS NULL OR COALESCE(o.year,m.provider_year)=@Year)
              AND EXISTS(SELECT 1 FROM movie_versions visible_versions WHERE visible_versions.movie_id=m.id)
            """;
        var args = new { ProfileId = profileId.ToString("N"), Search = search, Genre = NullIfBlank(query.Genre), query.Year, Limit = pageSize, Offset = (page - 1) * pageSize };
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var total = await connection.QuerySingleAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM movies m LEFT JOIN movie_overrides o ON o.movie_id=m.id {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<CatalogRow>(new CommandDefinition($"""
            SELECT m.id Id,COALESCE(o.title,m.provider_title) Title,COALESCE(o.year,m.provider_year) Year,
              m.overview Overview,m.local_poster_path PosterPath,m.created_at AddedAt,
              (SELECT COUNT(*) FROM movie_versions v JOIN media_files f ON f.id=v.media_file_id WHERE v.movie_id=m.id AND f.is_available=1) AvailableVersions,
              p.position_seconds ProgressSeconds,m.runtime_seconds DurationSeconds,
              COALESCE((SELECT group_concat(genre, char(31)) FROM (SELECT genre FROM movie_genres g WHERE g.movie_id=m.id ORDER BY genre)), '') Genres
            FROM movies m LEFT JOIN movie_overrides o ON o.movie_id=m.id
              LEFT JOIN movie_progress p ON p.movie_id=m.id AND p.profile_id=@ProfileId
            {where} ORDER BY {order} LIMIT @Limit OFFSET @Offset
            """, args, cancellationToken: cancellationToken));
        var genres = (await connection.QueryAsync<string>(new CommandDefinition("SELECT DISTINCT g.genre FROM movie_genres g WHERE EXISTS(SELECT 1 FROM movie_versions v WHERE v.movie_id=g.movie_id) ORDER BY g.genre COLLATE NOCASE", cancellationToken: cancellationToken))).ToList();
        var years = (await connection.QueryAsync<int>(new CommandDefinition("SELECT DISTINCT COALESCE(o.year,m.provider_year) FROM movies m LEFT JOIN movie_overrides o ON o.movie_id=m.id WHERE COALESCE(o.year,m.provider_year) IS NOT NULL AND EXISTS(SELECT 1 FROM movie_versions v WHERE v.movie_id=m.id) ORDER BY 1 DESC", cancellationToken: cancellationToken))).ToList();
        return new(rows.Select(ToItem).ToList(), total, page, pageSize, genres, years);
    }

    public async Task<MovieDetails?> GetAsync(Guid movieId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<DetailsRow>(new CommandDefinition("""
            SELECT m.id Id,COALESCE(o.title,m.provider_title) Title,COALESCE(o.year,m.provider_year) Year,
              m.original_title OriginalTitle,m.overview Overview,m.local_poster_path PosterPath,m.local_backdrop_path BackdropPath,
              p.position_seconds ProgressSeconds,COALESCE(p.revision,0) ProgressRevision,
              COALESCE((SELECT group_concat(genre, char(31)) FROM (SELECT genre FROM movie_genres g WHERE g.movie_id=m.id ORDER BY genre)), '') Genres
            FROM movies m LEFT JOIN movie_overrides o ON o.movie_id=m.id
              LEFT JOIN movie_progress p ON p.movie_id=m.id AND p.profile_id=@ProfileId WHERE m.id=@Id
            """, new { Id = movieId.ToString("N"), ProfileId = profileId.ToString("N") }, cancellationToken: cancellationToken));
        if (row is null) return null;
        var versions = await connection.QueryAsync<VersionRow>(new CommandDefinition("""
            SELECT f.id MediaFileId,f.relative_path RelativePath,f.container Container,f.video_codec VideoCodec,
              f.audio_codec AudioCodec,f.width Width,f.height Height,f.audio_channels AudioChannels,
              f.duration_seconds DurationSeconds,f.length Length,f.last_modified_at LastModifiedAt,f.is_available IsAvailable
            FROM movie_versions v JOIN media_files f ON f.id=v.media_file_id WHERE v.movie_id=@Id
            ORDER BY f.is_available DESC,COALESCE(f.height,0) DESC,f.relative_path COLLATE NOCASE
            """, new { Id = movieId.ToString("N") }, cancellationToken: cancellationToken));
        return new(Guid.ParseExact(row.Id, "N"), row.Title, ToInt(row.Year), row.OriginalTitle, row.Overview,
            ArtworkUrl(movieId, "poster", row.PosterPath), ArtworkUrl(movieId, "backdrop", row.BackdropPath), Split(row.Genres),
            versions.Select(ToVersion).ToList(), Seconds(row.ProgressSeconds), row.ProgressRevision);
    }

    public async Task<MediaStreamSource?> AuthorizeStreamAsync(Guid mediaFileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<StreamRow>(new CommandDefinition("""
            SELECT f.id MediaFileId,v.movie_id MovieId,f.root_path RootPath,f.relative_path RelativePath,
              f.length Length,f.last_modified_at LastModifiedAt,f.container Container
            FROM media_files f JOIN movie_versions v ON v.media_file_id=f.id
            WHERE f.id=@Id AND f.is_available=1
            """, new { Id = mediaFileId.ToString("N") }, cancellationToken: cancellationToken));
        if (row is null) return null;
        var root = Path.GetFullPath(row.RootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, row.RelativePath));
        if (!fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return null;
        var info = new FileInfo(fullPath);
        return new(mediaFileId, Guid.ParseExact(row.MovieId, "N"), fullPath, ContentType(row.Container, fullPath), info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    public async Task<ArtworkSource?> GetArtworkAsync(Guid movieId, string kind, CancellationToken cancellationToken = default)
    {
        if (kind is not ("poster" or "backdrop")) return null;
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var sql = kind == "poster" ? "SELECT local_poster_path FROM movies WHERE id=@Id" : "SELECT local_backdrop_path FROM movies WHERE id=@Id";
        var path = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(sql, new { Id = movieId.ToString("N") }, cancellationToken: cancellationToken));
        var fullPath = ResolveArtworkPath(path);
        if (fullPath is null || !File.Exists(fullPath)) return null;
        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return new(fullPath, contentType, new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)));
    }

    private string? ResolveArtworkPath(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;
        var artworkRoot = Path.GetFullPath(storagePaths.ArtworkPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.IsPathFullyQualified(storedPath)
            ? storedPath
            : Path.Combine(artworkRoot, storedPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(artworkRoot + Path.DirectorySeparatorChar, comparison) ? fullPath : null;
    }

    public async Task<PlaybackProgress?> GetProgressAsync(Guid profileId, Guid movieId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ProgressRow>(new CommandDefinition("SELECT position_seconds PositionSeconds,revision Revision,updated_at UpdatedAt FROM movie_progress WHERE profile_id=@ProfileId AND movie_id=@MovieId", Keys(profileId, movieId), cancellationToken: cancellationToken));
        return row is null ? null : new(profileId, movieId, TimeSpan.FromSeconds(row.PositionSeconds), row.Revision, Parse(row.UpdatedAt));
    }

    public async Task<PlaybackProgressResult> SaveAsync(Guid profileId, Guid movieId, TimeSpan position, long expectedRevision, string eventType, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        eventType = eventType is "play" or "pause" or "progress" or "ended" ? eventType : "progress";
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<ProgressRow>(new CommandDefinition("SELECT position_seconds PositionSeconds,revision Revision,updated_at UpdatedAt FROM movie_progress WHERE profile_id=@ProfileId AND movie_id=@MovieId", Keys(profileId, movieId), transaction, cancellationToken: cancellationToken));
        var accepted = current is null ? expectedRevision == 0 : current.Revision == expectedRevision;
        if (accepted)
        {
            var revision = expectedRevision + 1;
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO movie_progress(profile_id,movie_id,position_seconds,revision,updated_at) VALUES(@ProfileId,@MovieId,@Position,@Revision,@Now)
                ON CONFLICT(profile_id,movie_id) DO UPDATE SET position_seconds=excluded.position_seconds,revision=excluded.revision,updated_at=excluded.updated_at
                """, new { ProfileId = profileId.ToString("N"), MovieId = movieId.ToString("N"), Position = position.TotalSeconds, Revision = revision, Now = now }, transaction, cancellationToken: cancellationToken));
            if (eventType != "progress")
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO playback_events(id,profile_id,movie_id,event_type,position_seconds,occurred_at) VALUES(@Id,@ProfileId,@MovieId,@EventType,@Position,@Now)", new { Id = Guid.NewGuid().ToString("N"), ProfileId = profileId.ToString("N"), MovieId = movieId.ToString("N"), EventType = eventType, Position = position.TotalSeconds, Now = now }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM playback_events WHERE profile_id=@ProfileId AND id NOT IN (SELECT id FROM playback_events WHERE profile_id=@ProfileId ORDER BY occurred_at DESC,id DESC LIMIT @Limit)", new { ProfileId = profileId.ToString("N"), Limit = history.Value.MaximumEventsPerProfile }, transaction, cancellationToken: cancellationToken));
            current = new() { PositionSeconds = position.TotalSeconds, Revision = revision, UpdatedAt = now };
        }
        await transaction.CommitAsync(cancellationToken);
        current ??= new() { UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        return new(accepted, new(profileId, movieId, TimeSpan.FromSeconds(current.PositionSeconds), current.Revision, Parse(current.UpdatedAt)));
    }

    public async Task<IReadOnlyList<PlaybackEvent>> RecentActivityAsync(Guid profileId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EventRow>(new CommandDefinition("""
            SELECT e.id Id,e.movie_id MovieId,COALESCE(o.title,m.provider_title) MovieTitle,e.event_type EventType,e.position_seconds PositionSeconds,e.occurred_at OccurredAt
            FROM playback_events e JOIN movies m ON m.id=e.movie_id LEFT JOIN movie_overrides o ON o.movie_id=m.id
            WHERE e.profile_id=@ProfileId ORDER BY e.occurred_at DESC,e.id DESC LIMIT @Limit
            """, new { ProfileId = profileId.ToString("N"), Limit = Math.Clamp(limit, 1, 100) }, cancellationToken: cancellationToken));
        return rows.Select(x => new PlaybackEvent(Guid.ParseExact(x.Id,"N"), profileId, Guid.ParseExact(x.MovieId,"N"), x.MovieTitle, x.EventType, TimeSpan.FromSeconds(x.PositionSeconds), Parse(x.OccurredAt))).ToList();
    }

    private static object Keys(Guid profileId, Guid movieId) => new { ProfileId = profileId.ToString("N"), MovieId = movieId.ToString("N") };
    private static MovieCatalogItem ToItem(CatalogRow x) { var id=Guid.ParseExact(x.Id,"N"); return new(id,x.Title,ToInt(x.Year),x.Overview,ArtworkUrl(id,"poster",x.PosterPath),Split(x.Genres),checked((int)x.AvailableVersions),Parse(x.AddedAt),Seconds(x.ProgressSeconds),Seconds(x.DurationSeconds)); }
    private static MovieVersion ToVersion(VersionRow x) { var compatible=Compatible(x.Container,x.VideoCodec,x.AudioCodec); return new(Guid.ParseExact(x.MediaFileId,"N"),Label(x),x.Container??"unknown",x.VideoCodec,x.AudioCodec,ToInt(x.Width),ToInt(x.Height),ToInt(x.AudioChannels),Seconds(x.DurationSeconds),x.Length,Parse(x.LastModifiedAt),x.IsAvailable!=0,compatible,compatible?"Direct play is expected in modern browsers.":"This container or codec is not broadly supported by browsers; transcoding is not yet available."); }
    private static bool Compatible(string? c,string? v,string? a) => (c?.Contains("mp4",StringComparison.OrdinalIgnoreCase)==true || c?.Contains("webm",StringComparison.OrdinalIgnoreCase)==true || c?.Contains("mov",StringComparison.OrdinalIgnoreCase)==true) && v is ("h264" or "vp8" or "vp9" or "av1") && (a is null || a is "aac" or "mp3" or "opus" or "vorbis");
    private static string Label(VersionRow x) => $"{(x.Height is null ? "Unknown" : $"{x.Height}p")} · {(x.Container??"unknown").ToUpperInvariant()} · {Path.GetFileName(x.RelativePath)}";
    private static string ContentType(string? c,string p) => c?.Contains("webm",StringComparison.OrdinalIgnoreCase)==true||Path.GetExtension(p).Equals(".webm",StringComparison.OrdinalIgnoreCase)?"video/webm":Path.GetExtension(p).Equals(".ogg",StringComparison.OrdinalIgnoreCase)?"video/ogg":"video/mp4";
    private static string? ArtworkUrl(Guid id,string kind,string? path)=>string.IsNullOrWhiteSpace(path)?null:$"/artwork/{id:N}/{kind}";
    private static string[] Split(string value)=>string.IsNullOrEmpty(value)?[]:value.Split((char)31,StringSplitOptions.RemoveEmptyEntries);
    private static string? NullIfBlank(string? value)=>string.IsNullOrWhiteSpace(value)?null:value;
    private static int? ToInt(long? value)=>value is null?null:checked((int)value.Value);
    private static TimeSpan? Seconds(double? value)=>value is null?null:TimeSpan.FromSeconds(value.Value);
    private static DateTimeOffset Parse(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);

    private sealed class CatalogRow { public string Id{get;init;}=""; public string Title{get;init;}=""; public long? Year{get;init;} public string? Overview{get;init;} public string? PosterPath{get;init;} public string AddedAt{get;init;}=""; public long AvailableVersions{get;init;} public double? ProgressSeconds{get;init;} public double? DurationSeconds{get;init;} public string Genres{get;init;}=""; }
    private sealed class DetailsRow { public string Id{get;init;}=""; public string Title{get;init;}=""; public long? Year{get;init;} public string? OriginalTitle{get;init;} public string? Overview{get;init;} public string? PosterPath{get;init;} public string? BackdropPath{get;init;} public double? ProgressSeconds{get;init;} public long ProgressRevision{get;init;} public string Genres{get;init;}=""; }
    private sealed class VersionRow { public string MediaFileId{get;init;}=""; public string RelativePath{get;init;}=""; public string? Container{get;init;} public string? VideoCodec{get;init;} public string? AudioCodec{get;init;} public long? Width{get;init;} public long? Height{get;init;} public long? AudioChannels{get;init;} public double? DurationSeconds{get;init;} public long Length{get;init;} public string LastModifiedAt{get;init;}=""; public long IsAvailable{get;init;} }
    private sealed class StreamRow { public string MediaFileId{get;init;}=""; public string MovieId{get;init;}=""; public string RootPath{get;init;}=""; public string RelativePath{get;init;}=""; public long Length{get;init;} public string LastModifiedAt{get;init;}=""; public string? Container{get;init;} }
    private sealed class ProgressRow { public double PositionSeconds{get;init;} public long Revision{get;init;} public string UpdatedAt{get;init;}=""; }
    private sealed class EventRow { public string Id{get;init;}=""; public string MovieId{get;init;}=""; public string MovieTitle{get;init;}=""; public string EventType{get;init;}=""; public double PositionSeconds{get;init;} public string OccurredAt{get;init;}=""; }
}
