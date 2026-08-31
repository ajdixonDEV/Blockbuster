using System.Security.Claims;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Playback;
using Microsoft.AspNetCore.Antiforgery;

namespace Blockbuster;

internal static class PlaybackEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapMethods(
                "/media/{mediaFileId:guid}",
                ["GET", "HEAD"],
                StreamMediaAsync)
            .RequireAuthorization();

        app.MapGet("/artwork/{movieId:guid}/{kind}", GetArtworkAsync)
            .RequireAuthorization();

        app.MapPost("/api/movies/{movieId:guid}/progress", SaveProgressAsync)
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    private static async Task<IResult> StreamMediaAsync(
        Guid mediaFileId,
        IMovieLibrary library,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = await library.AuthorizeStreamAsync(
                mediaFileId,
                cancellationToken);
            return source is null
                ? Results.NotFound()
                : Results.File(
                    source.FullPath,
                    source.ContentType,
                    enableRangeProcessing: true,
                    lastModified: source.LastModified);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Results.Empty;
        }
    }

    private static async Task<IResult> GetArtworkAsync(
        Guid movieId,
        string kind,
        IMovieLibrary library,
        CancellationToken cancellationToken)
    {
        var source = await library.GetArtworkAsync(
            movieId,
            kind,
            cancellationToken);
        return source is null
            ? Results.NotFound()
            : Results.File(
                source.FullPath,
                source.ContentType,
                lastModified: source.LastModified);
    }

    private static async Task<IResult> SaveProgressAsync(
        Guid movieId,
        ProgressUpdate update,
        HttpContext context,
        IPlaybackProgressStore progress,
        CancellationToken cancellationToken)
    {
        var claim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var profileId))
        {
            return Results.Unauthorized();
        }

        if (!double.IsFinite(update.PositionSeconds)
            || update.PositionSeconds < 0)
        {
            return Results.BadRequest();
        }

        var result = await progress.SaveAsync(
            profileId,
            movieId,
            TimeSpan.FromSeconds(update.PositionSeconds),
            update.ExpectedRevision,
            update.EventType ?? "progress",
            cancellationToken);
        var response = new
        {
            revision = result.Current.Revision,
            positionSeconds = result.Current.Position.TotalSeconds
        };
        return result.Accepted
            ? Results.Ok(response)
            : Results.Conflict(response);
    }

    private sealed record ProgressUpdate(
        double PositionSeconds,
        long ExpectedRevision,
        string? EventType);
}
