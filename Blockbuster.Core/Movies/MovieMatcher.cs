namespace Blockbuster.Core.Movies;

public static class MovieMatcher
{
    public static MovieMatchDecision Decide(
        ParsedMovieFileName parsed,
        IReadOnlyList<MovieMetadataCandidate> candidates,
        bool providerConfigured)
    {
        if (parsed.Year is null)
            return new(MovieMatchOutcome.MissingYear, null, candidates, "A year is required for automatic matching.");
        if (!providerConfigured)
            return new(MovieMatchOutcome.ProviderUnavailable, null, candidates, "TMDB is not configured; review this file manually.");

        var normalizedTitle = MovieFilenameParser.NormalizeTitle(parsed.Title);
        var exact = candidates.Where(candidate =>
            candidate.Year == parsed.Year
            && MovieFilenameParser.NormalizeTitle(candidate.Title) == normalizedTitle).ToList();

        return exact.Count switch
        {
            1 => new(MovieMatchOutcome.Accepted, exact[0], candidates, "Unique normalized title and year match."),
            0 => new(MovieMatchOutcome.Unmatched, null, candidates, "No unique normalized title and matching-year result was found."),
            _ => new(MovieMatchOutcome.Ambiguous, null, candidates, "More than one normalized title and year result matched.")
        };
    }
}
