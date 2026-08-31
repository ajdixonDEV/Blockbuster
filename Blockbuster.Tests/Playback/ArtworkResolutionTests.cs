using Blockbuster.Core.Persistence;
using Blockbuster.Core.Playback;
using Blockbuster.Infrastructure;
using Blockbuster.Infrastructure.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blockbuster.Tests.Playback;

public sealed class ArtworkResolutionTests
{
    [Fact]
    public async Task ArtworkPathsResolveWithinConfiguredRootAndRejectEscapes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "blockbuster-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ffprobe = Path.Combine(root, "ffprobe-test");
            File.WriteAllText(ffprobe, "");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = root,
                ["MediaProbe:ExecutablePath"] = ffprobe
            }).Build();
            await using var services = new ServiceCollection().AddLogging()
                .AddBlockbusterInfrastructure(configuration).BuildServiceProvider();
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);

            var artworkRoot = Path.Combine(root, "artwork");
            var posterDirectory = Path.Combine(artworkRoot, "poster");
            Directory.CreateDirectory(posterDirectory);
            var relativePoster = Path.Combine("poster", "relative.jpg");
            var absolutePoster = Path.Combine(posterDirectory, "absolute.png");
            File.WriteAllBytes(Path.Combine(artworkRoot, relativePoster), [1, 2, 3]);
            File.WriteAllBytes(absolutePoster, [4, 5, 6]);
            var outsidePoster = Path.Combine(root, "outside.jpg");
            File.WriteAllBytes(outsidePoster, [7, 8, 9]);

            var relativeId = await InsertMovieAsync(services, relativePoster, cancellationToken);
            var absoluteId = await InsertMovieAsync(services, absolutePoster, cancellationToken);
            var outsideId = await InsertMovieAsync(services, outsidePoster, cancellationToken);
            var missingId = await InsertMovieAsync(services, Path.Combine("poster", "missing.jpg"), cancellationToken);
            var library = services.GetRequiredService<IMovieLibrary>();

            var relative = await library.GetArtworkAsync(relativeId, "poster", cancellationToken);
            var absolute = await library.GetArtworkAsync(absoluteId, "poster", cancellationToken);
            Assert.Equal(Path.GetFullPath(Path.Combine(artworkRoot, relativePoster)), relative?.FullPath);
            Assert.Equal("image/jpeg", relative?.ContentType);
            Assert.Equal(Path.GetFullPath(absolutePoster), absolute?.FullPath);
            Assert.Equal("image/png", absolute?.ContentType);
            Assert.Null(await library.GetArtworkAsync(outsideId, "poster", cancellationToken));
            Assert.Null(await library.GetArtworkAsync(missingId, "poster", cancellationToken));
            Assert.Null(await library.GetArtworkAsync(relativeId, "invalid", cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task<Guid> InsertMovieAsync(IServiceProvider services, string posterPath, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await services.GetRequiredService<IDbConnectionFactory>().OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO movies(id,provider_title,local_poster_path,created_at,updated_at) VALUES(@Id,'Artwork Test',@Poster,'now','now')",
            new
            {
                Id = id.ToString("N"),
                Poster = posterPath
            }, cancellationToken: cancellationToken));
        return id;
    }
}
