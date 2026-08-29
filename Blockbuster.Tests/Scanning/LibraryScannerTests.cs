using System.Data.Common;
using Blockbuster.Core.Media;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Persistence;
using Blockbuster.Core.Scanning;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blockbuster.Tests.Scanning;

public sealed class LibraryScannerTests
{
    [Fact]
    public async Task ScanMergesDuplicateVersionsReprobesChangesAndMarksMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = CreateTestRoot();
        var mediaRoot = Path.Combine(testRoot, "movies");
        Directory.CreateDirectory(Path.Combine(mediaRoot, "disc-a"));
        Directory.CreateDirectory(Path.Combine(mediaRoot, "disc-b"));
        var first = Path.Combine(mediaRoot, "disc-a", "Arrival (2016).mp4");
        var second = Path.Combine(mediaRoot, "disc-b", "Arrival (2016).mp4");
        await File.WriteAllTextAsync(first, "one", cancellationToken);
        await File.WriteAllTextAsync(second, "two", cancellationToken);
        var probe = new StubProbe();
        try
        {
            await using var services = CreateServices(testRoot, mediaRoot, probe, new StubMetadataProvider());
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);
            var scanner = services.GetRequiredService<ILibraryScanner>();

            var initial = await scanner.ScanAsync(ScanReason.Manual, cancellationToken);

            Assert.True(initial.Succeeded, Assert.Single(initial.Roots).Error);
            Assert.Equal(2, probe.Calls);
            var movieCount = await ScalarAsync(services, "SELECT COUNT(*) FROM movies", cancellationToken);
            var unexpectedPending = await services.GetRequiredService<IMovieCatalogStore>().ListPendingMatchesAsync(cancellationToken);
            Assert.True(movieCount == "1", $"Expected one movie; pending: {string.Join(" | ", unexpectedPending.Select(item => item.Outcome + ": " + item.Explanation))}");
            Assert.Equal("2", await ScalarAsync(services, "SELECT COUNT(*) FROM movie_versions", cancellationToken));

            var firstMediaId = Guid.ParseExact((await ScalarAsync(services, "SELECT id FROM media_files WHERE relative_path LIKE 'disc-a%'", cancellationToken))!, "N");
            await services.GetRequiredService<IMovieCatalogStore>().ApplyLocalOverrideAsync(firstMediaId, "My Arrival", 2016, cancellationToken);
            await File.AppendAllTextAsync(first, "-changed", cancellationToken);
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(2));
            await scanner.ScanAsync(ScanReason.Scheduled, cancellationToken);
            Assert.Equal(3, probe.Calls);
            Assert.Equal("My Arrival", await ScalarAsync(services, "SELECT title FROM movie_overrides", cancellationToken));

            File.Delete(second);
            var missing = await scanner.ScanAsync(ScanReason.Manual, cancellationToken);
            Assert.True(missing.Succeeded);
            Assert.Equal(1, Assert.Single(missing.Roots).MissingFiles);
            Assert.Equal("1", await ScalarAsync(services, "SELECT COUNT(*) FROM media_files WHERE is_available=0", cancellationToken));
            Assert.Equal("2", await ScalarAsync(services, "SELECT COUNT(*) FROM movie_versions", cancellationToken));
        }
        finally { DeleteTestRoot(testRoot); }
    }

    [Fact]
    public async Task UnavailableRootDoesNotMassMarkMediaMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = CreateTestRoot();
        var mediaRoot = Path.Combine(testRoot, "movies");
        Directory.CreateDirectory(mediaRoot);
        await File.WriteAllTextAsync(Path.Combine(mediaRoot, "Heat (1995).mp4"), "movie", cancellationToken);
        try
        {
            await using var services = CreateServices(testRoot, mediaRoot, new StubProbe(), new StubMetadataProvider("Heat", 1995, 949));
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);
            var scanner = services.GetRequiredService<ILibraryScanner>();
            var initial = await scanner.ScanAsync(ScanReason.Manual, cancellationToken);
            Assert.True(initial.Succeeded, Assert.Single(initial.Roots).Error);

            Directory.Move(mediaRoot, mediaRoot + "-offline");
            var failed = await scanner.ScanAsync(ScanReason.Scheduled, cancellationToken);

            Assert.False(failed.Succeeded);
            Assert.Equal(0, Assert.Single(failed.Roots).MissingFiles);
            Assert.Equal("1", await ScalarAsync(services, "SELECT COUNT(*) FROM media_files WHERE is_available=1", cancellationToken));
            Assert.Equal("1", await ScalarAsync(services, "SELECT COUNT(*) FROM library_scan_runs WHERE succeeded=0", cancellationToken));
        }
        finally { DeleteTestRoot(testRoot); }
    }

    [Fact]
    public async Task ProbeFailureAndAmbiguousMetadataBecomeReviewItems()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = CreateTestRoot();
        var mediaRoot = Path.Combine(testRoot, "movies");
        Directory.CreateDirectory(mediaRoot);
        await File.WriteAllTextAsync(Path.Combine(mediaRoot, "Corrupt (2020).mp4"), "bad", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(mediaRoot, "Heat (1995).mp4"), "good", cancellationToken);
        var metadata = new StubMetadataProvider("Heat", 1995, 1) { ReturnAmbiguous = true };
        try
        {
            await using var services = CreateServices(testRoot, mediaRoot, new StubProbe("Corrupt"), metadata);
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);

            var result = await services.GetRequiredService<ILibraryScanner>().ScanAsync(ScanReason.Manual, cancellationToken);
            var pending = await services.GetRequiredService<IMovieCatalogStore>().ListPendingMatchesAsync(cancellationToken);

            Assert.True(result.Succeeded);
            Assert.Equal(2, pending.Count);
            Assert.Contains(pending, item => item.Outcome == MovieMatchOutcome.ProbeFailed);
            Assert.Contains(pending, item => item.Outcome == MovieMatchOutcome.Ambiguous);
        }
        finally { DeleteTestRoot(testRoot); }
    }

    [Fact]
    public async Task MissingYearIsQueuedWithoutCallingTmdb()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = CreateTestRoot();
        var mediaRoot = Path.Combine(testRoot, "movies");
        Directory.CreateDirectory(mediaRoot);
        await File.WriteAllTextAsync(Path.Combine(mediaRoot, "Casablanca.mp4"), "movie", cancellationToken);
        var metadata = new StubMetadataProvider();
        try
        {
            await using var services = CreateServices(testRoot, mediaRoot, new StubProbe(), metadata);
            await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);
            await services.GetRequiredService<ILibraryScanner>().ScanAsync(ScanReason.Manual, cancellationToken);

            Assert.Equal(0, metadata.SearchCalls);
            var pending = Assert.Single(await services.GetRequiredService<IMovieCatalogStore>().ListPendingMatchesAsync(cancellationToken));
            Assert.Equal(MovieMatchOutcome.MissingYear, pending.Outcome);
        }
        finally { DeleteTestRoot(testRoot); }
    }

    private static ServiceProvider CreateServices(string testRoot, string mediaRoot, IMediaProbe probe, IMovieMetadataProvider metadata)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataRoot"] = testRoot,
            ["Libraries:Sources:0:Id"] = "movies-main",
            ["Libraries:Sources:0:MovieRoots:0"] = mediaRoot,
            ["Scanning:ScanOnStartup"] = "false",
            ["Scanning:Extensions:0"] = ".mp4",
            ["Scanning:Concurrency"] = "2",
            ["MediaProbe:ExecutablePath"] = "stub-ffprobe",
            ["Tmdb:Token"] = "stub-token"
        }).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddBlockbusterConfiguration(configuration);
        collection.AddSingleton(probe);
        collection.AddSingleton(metadata);
        collection.AddSingleton<IArtworkCache, StubArtworkCache>();
        return collection.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<string?> ScalarAsync(IServiceProvider services, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await services.GetRequiredService<IDbConnectionFactory>().OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "blockbuster-scan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestRoot(string testRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
    }

    private sealed class StubProbe(string? failingName = null) : IMediaProbe
    {
        private int _calls;
        public int Calls => _calls;
        public Task<MediaProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (failingName is not null && Path.GetFileName(absolutePath).Contains(failingName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("stub corrupt media");
            return Task.FromResult(new MediaProbeResult(TimeSpan.FromMinutes(100), "mp4", "h264", "aac", 1920, 1080, 2));
        }
    }

    private sealed class StubMetadataProvider(string title = "Arrival", int year = 2016, int tmdbId = 329865) : IMovieMetadataProvider
    {
        private int _searchCalls;
        public int SearchCalls => _searchCalls;
        public bool ReturnAmbiguous { get; init; }
        public bool IsConfigured => true;
        public Task<IReadOnlyList<MovieMetadataCandidate>> SearchAsync(string searchTitle, int searchYear, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searchCalls);
            IReadOnlyList<MovieMetadataCandidate> result = ReturnAmbiguous
                ? [new(tmdbId, title, year, null, null, null), new(tmdbId + 1, title, year, null, null, null)]
                : [new(tmdbId, title, year, null, null, null)];
            return Task.FromResult(result);
        }
        public Task<MovieMetadata?> GetAsync(int requestedId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MovieMetadata?>(new(requestedId, title, title, year, "Overview", TimeSpan.FromMinutes(100), null, null, ["Drama"]));
    }

    private sealed class StubArtworkCache : IArtworkCache
    {
        public Task<string?> CacheAsync(string kind, int tmdbId, string? providerPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
