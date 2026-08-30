using Blockbuster.Core.Movies;

namespace Blockbuster.Infrastructure.Movies;

public sealed class MovieMatchResolver(IMovieCatalogStore catalog, IMovieMetadataProvider metadata, IArtworkCache artwork) : IMovieMatchResolver
{
    public async Task<MovieResolutionResult> ResolveAutomaticAsync(Guid mediaFileId, ParsedMovieFileName parsed, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAutomaticAsync(parsed, cancellationToken);
        if (prepared.Metadata is null)
        {
            await catalog.QueuePendingMatchAsync(mediaFileId, parsed, prepared.PendingDecision!, cancellationToken);
            return new(false, true, null);
        }
        await catalog.ApplyMetadataAsync(mediaFileId, prepared.Metadata, prepared.LocalPosterPath, prepared.LocalBackdropPath, cancellationToken);
        return new(true, false, null);
    }

    public async Task<ScanMatchResolution> PrepareAutomaticAsync(ParsedMovieFileName parsed, CancellationToken cancellationToken = default)
    {
        if (parsed.Year is null || !metadata.IsConfigured)
            return new(parsed, MovieMatcher.Decide(parsed, [], metadata.IsConfigured), null, null, null);
        IReadOnlyList<MovieMetadataCandidate> candidates;
        try { candidates = await metadata.SearchAsync(parsed.Title, parsed.Year.Value, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(parsed, new(MovieMatchOutcome.ProviderUnavailable, null, [], "TMDB could not be reached; retry matching later."), null, null, null);
        }
        var decision = MovieMatcher.Decide(parsed, candidates, metadata.IsConfigured);
        if (decision.Accepted is null) return new(parsed, decision, null, null, null);
        try
        {
            var movie = await metadata.GetAsync(decision.Accepted.TmdbId, cancellationToken);
            if (movie is null) return new(parsed, decision with { Outcome = MovieMatchOutcome.ProviderUnavailable, Accepted = null, Explanation = "TMDB details were unavailable; retry matching later." }, null, null, null);
            var poster = CacheIndependentlyAsync("poster", movie.TmdbId, movie.PosterPath, cancellationToken);
            var backdrop = CacheIndependentlyAsync("backdrop", movie.TmdbId, movie.BackdropPath, cancellationToken);
            await Task.WhenAll(poster, backdrop);
            return new(parsed, null, movie, poster.Result, backdrop.Result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(parsed, decision with { Outcome = MovieMatchOutcome.ProviderUnavailable, Accepted = null, Explanation = "TMDB details could not be loaded; retry matching later." }, null, null, null);
        }
    }

    public Task<MovieResolutionResult> ResolveProviderSelectionAsync(Guid mediaFileId, int tmdbId, CancellationToken cancellationToken = default) =>
        tmdbId <= 0 ? Task.FromResult(new MovieResolutionResult(false, true, "A provider ID is required.")) : ResolveMetadataAsync(mediaFileId, tmdbId, null, null, cancellationToken);

    public async Task<MovieResolutionResult> ResolveLocalMetadataAsync(Guid mediaFileId, string title, int? year, CancellationToken cancellationToken = default)
    {
        try { await catalog.ApplyLocalOverrideAsync(mediaFileId, title, year, cancellationToken); return new(true, false, null); }
        catch (ArgumentException exception) { return new(false, true, exception.Message); }
    }

    private async Task<MovieResolutionResult> ResolveMetadataAsync(Guid mediaFileId, int tmdbId, ParsedMovieFileName? parsed, MovieMatchDecision? decision, CancellationToken cancellationToken)
    {
        try
        {
            var movie = await metadata.GetAsync(tmdbId, cancellationToken);
            if (movie is null) return await KeepPendingAsync(mediaFileId, parsed, decision, "TMDB details were unavailable; retry matching later.", cancellationToken);
            var posterTask = CacheIndependentlyAsync("poster", movie.TmdbId, movie.PosterPath, cancellationToken);
            var backdropTask = CacheIndependentlyAsync("backdrop", movie.TmdbId, movie.BackdropPath, cancellationToken);
            await Task.WhenAll(posterTask, backdropTask);
            await catalog.ApplyMetadataAsync(mediaFileId, movie, posterTask.Result, backdropTask.Result, cancellationToken);
            return new(true, false, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (parsed is null) return new(false, true, "TMDB could not be reached. The pending match was left unchanged.");
            return await KeepPendingAsync(mediaFileId, parsed, decision, "TMDB details could not be loaded; retry matching later.", cancellationToken);
        }
    }

    private async Task<MovieResolutionResult> KeepPendingAsync(Guid mediaFileId, ParsedMovieFileName? parsed, MovieMatchDecision? decision, string message, CancellationToken cancellationToken)
    {
        if (parsed is not null && decision is not null)
            await catalog.QueuePendingMatchAsync(mediaFileId, parsed, decision with { Outcome = MovieMatchOutcome.ProviderUnavailable, Accepted = null, Explanation = message }, cancellationToken);
        return new(false, true, message);
    }

    private async Task<string?> CacheIndependentlyAsync(string kind, int tmdbId, string? path, CancellationToken cancellationToken)
    {
        try { return await artwork.CacheAsync(kind, tmdbId, path, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
    }
}
