using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Blockbuster.Core.Movies;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Movies;

public sealed class TmdbMovieMetadataProvider(HttpClient httpClient, IOptions<TmdbOptions> options) : IMovieMetadataProvider
{
    private readonly TmdbOptions _options = options.Value;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Token);

    public async Task<IReadOnlyList<MovieMetadataCandidate>> SearchAsync(string title, int year, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return [];
        using var request = CreateRequest($"search/movie?query={Uri.EscapeDataString(title)}&year={year.ToString(CultureInfo.InvariantCulture)}&language={Uri.EscapeDataString(_options.Locale)}&include_adult=false");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray().Select(result => new MovieMetadataCandidate(
            result.GetProperty("id").GetInt32(),
            GetString(result, "title") ?? string.Empty,
            ParseYear(GetString(result, "release_date")),
            GetString(result, "overview"),
            GetString(result, "poster_path"),
            GetString(result, "backdrop_path"))).ToList();
    }

    public async Task<MovieMetadata?> GetAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;
        using var request = CreateRequest($"movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}?language={Uri.EscapeDataString(_options.Locale)}");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var movie = document.RootElement;
        var runtime = movie.TryGetProperty("runtime", out var runtimeValue) && runtimeValue.TryGetInt32(out var minutes)
            ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;
        var genres = movie.TryGetProperty("genres", out var genreValues)
            ? genreValues.EnumerateArray().Select(genre => GetString(genre, "name")).OfType<string>().ToList()
            : [];
        return new MovieMetadata(
            movie.GetProperty("id").GetInt32(), GetString(movie, "title") ?? string.Empty,
            GetString(movie, "original_title"), ParseYear(GetString(movie, "release_date")),
            GetString(movie, "overview"), runtime, GetString(movie, "poster_path"),
            GetString(movie, "backdrop_path"), genres);
    }

    private HttpRequestMessage CreateRequest(string relativePath)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ParseYear(string? releaseDate) =>
        releaseDate is { Length: >= 4 } && int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
}
