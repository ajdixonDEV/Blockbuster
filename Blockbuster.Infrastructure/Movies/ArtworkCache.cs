using Blockbuster.Core.Movies;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Movies;

public sealed class ArtworkCache(HttpClient httpClient, IStoragePathResolver paths, IOptions<TmdbOptions> options) : IArtworkCache
{
    private readonly TmdbOptions _options = options.Value;

    public async Task<string?> CacheAsync(string kind, int tmdbId, string? providerPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerPath))
            return null;
        if (kind is not ("poster" or "backdrop"))
            throw new ArgumentException("Artwork kind must be poster or backdrop.", nameof(kind));
        var size = kind == "poster" ? _options.PosterSize : _options.BackdropSize;
        var extension = Path.GetExtension(providerPath);
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            extension = ".jpg";
        var directory = Path.Combine(paths.ArtworkPath, kind);
        Directory.CreateDirectory(directory);
        var relativePath = Path.Combine(kind, tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) + extension.ToLowerInvariant());
        var destination = Path.Combine(paths.ArtworkPath, relativePath);
        if (File.Exists(destination))
            return relativePath;

        var source = new Uri($"https://image.tmdb.org/t/p/{Uri.EscapeDataString(size)}/{providerPath.TrimStart('/')}");
        using var response = await httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await response.Content.CopyToAsync(target, cancellationToken);
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return relativePath;
    }
}
