using Blockbuster.Core.Profiles;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Scanning;
using Blockbuster.Core.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Data.Sqlite;

namespace Blockbuster;

internal static class AdministrationEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app
            .MapGroup("/admin")
            .RequireAuthorization("Administrator")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        group.MapPost("/profiles/create", CreateProfileAsync);
        group.MapPost("/profiles/update", UpdateProfileAsync);
        group.MapPost("/profiles/delete", DeleteProfileAsync);
        group.MapPost("/scans/request", RequestScanAsync);
        group.MapPost("/matches/accept", AcceptMatchAsync);
        group.MapPost("/matches/local", ApplyLocalMatchAsync);
    }

    private static async Task<IResult> CreateProfileAsync(
        HttpContext context,
        IProfileStore profiles,
        IPinHasher hasher)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var pin = form["pin"].ToString();
        try
        {
            await profiles.CreateAsync(
                form["name"].ToString(),
                string.IsNullOrEmpty(pin) ? null : hasher.Hash(pin),
                context.RequestAborted);
        }
        catch (Exception exception)
            when (exception is ArgumentException or SqliteException)
        {
            return InvalidProfileRedirect();
        }

        return Results.LocalRedirect("/admin");
    }

    private static async Task<IResult> UpdateProfileAsync(
        HttpContext context,
        IProfileStore profiles,
        IPinHasher hasher)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Guid.TryParse(form["id"], out var id))
        {
            return Results.BadRequest();
        }

        var pin = form["pin"].ToString();
        try
        {
            await profiles.UpdateAsync(
                id,
                form["name"].ToString(),
                string.IsNullOrEmpty(pin) ? null : hasher.Hash(pin),
                form["clearPin"] == "on",
                context.RequestAborted);
        }
        catch (Exception exception)
            when (exception is ArgumentException or SqliteException)
        {
            return InvalidProfileRedirect();
        }

        return Results.LocalRedirect("/admin");
    }

    private static async Task<IResult> DeleteProfileAsync(
        HttpContext context,
        IProfileStore profiles)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (Guid.TryParse(form["id"], out var id))
        {
            await profiles.DeleteAsync(id, context.RequestAborted);
        }

        return Results.LocalRedirect("/admin");
    }

    private static async Task<IResult> RequestScanAsync(
        HttpContext context,
        ILibraryScanner scanner)
    {
        var result = await scanner.ScanAsync(
            ScanReason.Manual,
            context.RequestAborted);
        if (!result.Succeeded)
        {
            const string message =
                "One or more movie roots could not be scanned. "
                + "Existing availability was preserved for failed roots.";
            return Results.LocalRedirect(
                "/admin?error=" + Uri.EscapeDataString(message));
        }

        return Results.LocalRedirect("/admin");
    }

    private static async Task<IResult> AcceptMatchAsync(
        HttpContext context,
        IMovieMatchResolver resolver)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Guid.TryParse(form["mediaFileId"], out var mediaFileId)
            || !int.TryParse(form["tmdbId"], out var tmdbId)
            || tmdbId <= 0)
        {
            return Results.BadRequest();
        }

        var result = await resolver.ResolveProviderSelectionAsync(
            mediaFileId,
            tmdbId,
            context.RequestAborted);
        return MatchResult(result, "That TMDB movie could not be found.");
    }

    private static async Task<IResult> ApplyLocalMatchAsync(
        HttpContext context,
        IMovieMatchResolver resolver)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Guid.TryParse(form["mediaFileId"], out var mediaFileId))
        {
            return Results.BadRequest();
        }

        int? year = int.TryParse(form["year"], out var parsedYear)
            ? parsedYear
            : null;
        var result = await resolver.ResolveLocalMetadataAsync(
            mediaFileId,
            form["title"].ToString(),
            year,
            context.RequestAborted);
        return MatchResult(result, "The local title or year is invalid.");
    }

    private static IResult InvalidProfileRedirect() =>
        Results.LocalRedirect(
            "/admin?error="
            + Uri.EscapeDataString(
                "Profile name or PIN is invalid or already used."));

    private static IResult MatchResult(
        MovieResolutionResult result,
        string fallbackMessage) =>
        result.Succeeded
            ? Results.LocalRedirect("/admin")
            : Results.LocalRedirect(
                "/admin?error="
                + Uri.EscapeDataString(result.Message ?? fallbackMessage));
}
