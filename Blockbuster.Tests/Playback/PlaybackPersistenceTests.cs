using Blockbuster.Core.Persistence;
using Blockbuster.Core.Playback;
using Blockbuster.Infrastructure;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blockbuster.Tests.Playback;

public sealed class PlaybackPersistenceTests
{
    [Fact]
    public async Task ProgressRejectsStaleRevisionAndTrimsHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "blockbuster-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ffprobe = Path.Combine(root, "ffprobe-test"); File.WriteAllText(ffprobe, "");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["Storage:DataRoot"] = root, ["MediaProbe:ExecutablePath"] = ffprobe,
                ["Tmdb:Token"] = "test", ["History:MaximumEventsPerProfile"] = "2"
            }).Build();
            var services = new ServiceCollection().AddLogging().AddBlockbusterInfrastructure(configuration).BuildServiceProvider();
            await using (services)
            {
                await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);
                var profileId=Guid.NewGuid();var movieId=Guid.NewGuid();
                await using(var connection=await services.GetRequiredService<IDbConnectionFactory>().OpenConnectionAsync(cancellationToken))
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO profiles(id,name,created_at,updated_at) VALUES(@Profile,'Viewer','now','now');
                        INSERT INTO movies(id,provider_title,created_at,updated_at) VALUES(@Movie,'Test Movie','now','now');
                        """,new{Profile=profileId.ToString("N"),Movie=movieId.ToString("N")},cancellationToken:cancellationToken));
                var store=services.GetRequiredService<IPlaybackProgressStore>();
                var first=await store.SaveAsync(profileId,movieId,TimeSpan.FromSeconds(40),0,"play",cancellationToken);
                var stale=await store.SaveAsync(profileId,movieId,TimeSpan.FromSeconds(5),0,"pause",cancellationToken);
                var second=await store.SaveAsync(profileId,movieId,TimeSpan.FromSeconds(50),1,"pause",cancellationToken);
                var third=await store.SaveAsync(profileId,movieId,TimeSpan.FromSeconds(60),2,"ended",cancellationToken);
                Assert.True(first.Accepted);Assert.False(stale.Accepted);Assert.True(second.Accepted);Assert.True(third.Accepted);
                Assert.Equal(60,third.Current.Position.TotalSeconds);Assert.Equal(3,third.Current.Revision);
                var events=await services.GetRequiredService<IMovieLibrary>().RecentActivityAsync(profileId,10,cancellationToken);
                Assert.Equal(2,events.Count);Assert.Equal(["ended","pause"],events.Select(x=>x.EventType));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
}
