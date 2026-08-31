using Blockbuster.Core.Movies;

namespace Blockbuster.Infrastructure.Movies;

internal sealed record PreparedMovieMatch(
    ParsedMovieFileName Parsed,
    MovieMatchDecision? PendingDecision,
    MovieMetadata? Metadata,
    string? LocalPosterPath,
    string? LocalBackdropPath);

internal interface IAutomaticMovieMatchPreparer
{
    Task<PreparedMovieMatch> PrepareAsync(
        ParsedMovieFileName parsed,
        CancellationToken cancellationToken = default);
}

internal sealed class MovieMatchResolver(
    IMovieMatchTransitionStore catalog,
    IMovieMetadataProvider metadata,
    IArtworkCache artwork) : IMovieMatchResolver, IAutomaticMovieMatchPreparer
{
    public async Task<PreparedMovieMatch> PrepareAsync(
        ParsedMovieFileName parsed,
        CancellationToken cancellationToken = default)
    {
        if (parsed.Year is null || !metadata.IsConfigured)
        {
            return new PreparedMovieMatch(
                parsed,
                MovieMatcher.Decide(parsed, [], metadata.IsConfigured),
                null,
                null,
                null);
        }

        IReadOnlyList<MovieMetadataCandidate> candidates;
        try
        {
            candidates = await metadata.SearchAsync(
                parsed.Title,
                parsed.Year.Value,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PendingProviderFailure(
                parsed,
                "TMDB could not be reached; retry matching later.");
        }

        var decision = MovieMatcher.Decide(
            parsed,
            candidates,
            metadata.IsConfigured);
        if (decision.Accepted is null)
        {
            return new PreparedMovieMatch(parsed, decision, null, null, null);
        }

        try
        {
            var movie = await metadata.GetAsync(
                decision.Accepted.TmdbId,
                cancellationToken);
            if (movie is null)
            {
                return PendingProviderFailure(
                    parsed,
                    "TMDB details were unavailable; retry matching later.",
                    decision);
            }

            var poster = CacheIndependentlyAsync(
                "poster",
                movie.TmdbId,
                movie.PosterPath,
                cancellationToken);
            var backdrop = CacheIndependentlyAsync(
                "backdrop",
                movie.TmdbId,
                movie.BackdropPath,
                cancellationToken);
            await Task.WhenAll(poster, backdrop);
            return new PreparedMovieMatch(
                parsed,
                null,
                movie,
                poster.Result,
                backdrop.Result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PendingProviderFailure(
                parsed,
                "TMDB details could not be loaded; retry matching later.",
                decision);
        }
    }

    public Task<MovieResolutionResult> ResolveProviderSelectionAsync(
        Guid mediaFileId,
        int tmdbId,
        CancellationToken cancellationToken = default)
    {
        return tmdbId <= 0
            ? Task.FromResult(
                new MovieResolutionResult(
                    false,
                    true,
                    "A provider ID is required."))
            : ResolveMetadataAsync(mediaFileId, tmdbId, cancellationToken);
    }

    public async Task<MovieResolutionResult> ResolveLocalMetadataAsync(
        Guid mediaFileId,
        string title,
        int? year,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await catalog.ApplyLocalAssociationAsync(
                mediaFileId,
                title,
                year,
                cancellationToken);
            return new MovieResolutionResult(true, false, null);
        }
        catch (ArgumentException exception)
        {
            return new MovieResolutionResult(false, true, exception.Message);
        }
    }

    private async Task<MovieResolutionResult> ResolveMetadataAsync(
        Guid mediaFileId,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            var movie = await metadata.GetAsync(tmdbId, cancellationToken);
            if (movie is null)
            {
                return new MovieResolutionResult(
                    false,
                    true,
                    "TMDB details were unavailable; retry matching later.");
            }

            var posterTask = CacheIndependentlyAsync(
                "poster",
                movie.TmdbId,
                movie.PosterPath,
                cancellationToken);
            var backdropTask = CacheIndependentlyAsync(
                "backdrop",
                movie.TmdbId,
                movie.BackdropPath,
                cancellationToken);
            await Task.WhenAll(posterTask, backdropTask);
            await catalog.ApplyMetadataAssociationAsync(
                mediaFileId,
                movie,
                posterTask.Result,
                backdropTask.Result,
                cancellationToken);
            return new MovieResolutionResult(true, false, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MovieResolutionResult(
                false,
                true,
                "TMDB could not be reached. The pending match was left unchanged.");
        }
    }

    private async Task<string?> CacheIndependentlyAsync(
        string kind,
        int tmdbId,
        string? path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await artwork.CacheAsync(
                kind,
                tmdbId,
                path,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static PreparedMovieMatch PendingProviderFailure(
        ParsedMovieFileName parsed,
        string message,
        MovieMatchDecision? decision = null)
    {
        var pending = decision is null
            ? new MovieMatchDecision(
                MovieMatchOutcome.ProviderUnavailable,
                null,
                [],
                message)
            : decision with
            {
                Outcome = MovieMatchOutcome.ProviderUnavailable,
                Accepted = null,
                Explanation = message
            };
        return new PreparedMovieMatch(parsed, pending, null, null, null);
    }
}
