using System.Security.Claims;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Playback;
using Blockbuster.Core.SharedPlayback;
using Blockbuster.SharedPlayback;
using Microsoft.AspNetCore.Antiforgery;

namespace Blockbuster;

internal static class SharedRoomEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/shared/new", CreateRoomAsync)
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        app.MapHub<SharedPlaybackHub>("/hubs/shared-playback");
    }

    private static async Task<IResult> CreateRoomAsync(
        HttpContext context,
        IMovieLibrary library,
        ISharedPlaybackCoordinator rooms,
        CancellationToken cancellationToken)
    {
        var claim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var profileId))
        {
            return Results.Unauthorized();
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!Guid.TryParse(form["movie"], out var movieId)
            || !Guid.TryParse(form["file"], out var mediaFileId))
        {
            return Results.BadRequest();
        }

        var movie = await library.GetAsync(movieId, profileId, cancellationToken);
        var version = movie?.Versions.FirstOrDefault(item =>
            item.MediaFileId == mediaFileId && item.IsAvailable);
        if (movie is null || version is null)
        {
            return Results.BadRequest();
        }

        var room = rooms.CreateRoom(movie.Id, version.MediaFileId, movie.Title);
        return Results.LocalRedirect($"/shared/{room.RoomId}");
    }
}
