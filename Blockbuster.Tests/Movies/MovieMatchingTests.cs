using Blockbuster.Core.Movies;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Movies;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using Xunit;

namespace Blockbuster.Tests.Movies;

public sealed class MovieMatchingTests
{
    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.mkv", "The Matrix", 1999)]
    [InlineData("Movies/Arrival (2016).mp4", "Arrival", 2016)]
    [InlineData("Dune_Part_Two_2024_WEB-DL.mkv", "Dune Part Two", 2024)]
    [InlineData("Casablanca.1080p.BluRay.mkv", "Casablanca", null)]
    public void ParsesTitleAndOptionalYear(string path, string expectedTitle, int? expectedYear)
    {
        var parsed = MovieFilenameParser.Parse(path);
        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedYear, parsed.Year);
    }

    [Fact]
    public void AcceptsOnlyOneNormalizedTitleAndYearMatch()
    {
        var parsed = new ParsedMovieFileName("Amélie", 2001);
        var candidates = new[]
        {
            new MovieMetadataCandidate(1, "Amelie", 2001, null, null, null),
            new MovieMetadataCandidate(2, "Amelia", 2009, null, null, null)
        };

        var decision = MovieMatcher.Decide(parsed, candidates, providerConfigured: true);

        Assert.Equal(MovieMatchOutcome.Accepted, decision.Outcome);
        Assert.Equal(1, decision.Accepted?.TmdbId);
    }

    [Fact]
    public void QueuesMissingYearAndAmbiguousMatches()
    {
        var candidate = new MovieMetadataCandidate(1, "Heat", 1995, null, null, null);

        Assert.Equal(MovieMatchOutcome.MissingYear,
            MovieMatcher.Decide(new("Heat", null), [candidate], providerConfigured: true).Outcome);
        Assert.Equal(MovieMatchOutcome.Ambiguous,
            MovieMatcher.Decide(new("Heat", 1995), [candidate, candidate with { TmdbId = 2 }], providerConfigured: true).Outcome);
    }

    [Fact]
    public async Task TmdbSearchUsesBearerTokenAndParsesCandidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler("""
            {"results":[{"id":949,"title":"Heat","release_date":"1995-12-15","overview":"A crime saga","poster_path":"/poster.jpg","backdrop_path":"/backdrop.jpg"}]}
            """);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var provider = new TmdbMovieMetadataProvider(client, Options.Create(new TmdbOptions { Token = "secret-token", Locale = "en-US" }));

        var result = Assert.Single(await provider.SearchAsync("Heat", 1995, cancellationToken));

        Assert.Equal(949, result.TmdbId);
        Assert.Equal(1995, result.Year);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-token", handler.AuthorizationParameter);
        Assert.Contains("search/movie", handler.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("query=Heat", handler.RequestUri?.Query, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? AuthorizationScheme
        {
            get; private set;
        }
        public string? AuthorizationParameter
        {
            get; private set;
        }
        public Uri? RequestUri
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }
}
