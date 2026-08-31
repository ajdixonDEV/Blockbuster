using Blockbuster.Core.SharedPlayback;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.SharedPlayback;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Blockbuster.Tests.SharedPlayback;

public sealed class SharedPlaybackCoordinatorTests
{
    [Fact]
    public void CommandsAreSerializedAndLastAcceptedStateIsAuthoritative()
    {
        using var coordinator = CreateCoordinator();
        var room = coordinator.CreateRoom(Guid.NewGuid(), Guid.NewGuid(), "Arrival");
        using var firstSession = coordinator.JoinRoom(room.RoomId, "Alex")!;
        using var secondSession = coordinator.JoinRoom(room.RoomId, "Sam")!;

        var first = firstSession.Apply(new(false, 42));
        var second = secondSession.Apply(new(true, 57));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Revision + 1, second.Revision);
        Assert.True(second.IsPaused);
        Assert.Equal(57, second.AnchorPositionSeconds);
        Assert.Equal("Sam", second.LastControllingProfile);
        Assert.Equal(["Alex", "Sam"], second.Participants);
    }

    [Fact]
    public void RejoinReceivesCurrentSnapshotAndRoomSurvivesCreatorLeaving()
    {
        using var coordinator = CreateCoordinator();
        var room = coordinator.CreateRoom(Guid.NewGuid(), Guid.NewGuid(), "Heat");
        using var creator = coordinator.JoinRoom(room.RoomId, "Alex")!;
        var expected = creator.Apply(new(false, 20));
        creator.Leave();

        using var rejoinedSession = coordinator.JoinRoom(room.RoomId, "Alex")!;
        var rejoined = coordinator.GetSnapshot(room.RoomId);

        Assert.NotNull(rejoined);
        Assert.Equal(expected!.Revision, rejoined.Revision);
        Assert.Equal(expected.AnchorPositionSeconds, rejoined.AnchorPositionSeconds);
        Assert.False(rejoined.IsPaused);
    }

    [Fact]
    public void InvalidCommandsDoNotMutateRoom()
    {
        using var coordinator = CreateCoordinator();
        var room = coordinator.CreateRoom(Guid.NewGuid(), Guid.NewGuid(), "Movie");
        using var session = coordinator.JoinRoom(room.RoomId, "Viewer")!;

        Assert.Null(session.Apply(new(false, double.NaN)));
        Assert.Equal(0, coordinator.GetSnapshot(room.RoomId)!.Revision);
    }

    [Fact]
    public void UnjoinedAndLeftSessionsCannotIssueCommands()
    {
        using var coordinator = CreateCoordinator();
        var room = coordinator.CreateRoom(Guid.NewGuid(), Guid.NewGuid(), "Movie");
        using var session = coordinator.JoinRoom(room.RoomId, "Viewer")!;
        session.Leave();

        Assert.Null(session.Apply(new(false, 10)));
        Assert.Equal(0, coordinator.GetSnapshot(room.RoomId)!.Revision);
    }

    [Fact]
    public void BufferingPausesTheRoomUntilEveryBufferingViewerIsReady()
    {
        using var coordinator = CreateCoordinator();
        var room = coordinator.CreateRoom(Guid.NewGuid(), Guid.NewGuid(), "Movie");
        using var alex = coordinator.JoinRoom(room.RoomId, "Alex")!;
        using var sam = coordinator.JoinRoom(room.RoomId, "Sam")!;
        alex.Apply(new(false, 30));

        var waiting = sam.SetBuffering(true, 31);
        Assert.NotNull(waiting);
        Assert.True(waiting.IsPaused);
        Assert.Equal(31, waiting.AnchorPositionSeconds);

        alex.SetBuffering(true, 31);
        var stillWaiting = sam.SetBuffering(false, 31);
        Assert.True(stillWaiting!.IsPaused);

        var resumed = alex.SetBuffering(false, 31);
        Assert.False(resumed!.IsPaused);
        Assert.Equal(31, resumed.AnchorPositionSeconds);
    }

    [Fact]
    public void EmptyRoomExpiresOnlyAfterConfiguredBoundary()
    {
        var clock = new FakeTimeProvider();
        using var coordinator = CreateCoordinator(clock);
        var room = coordinator.CreateRoom(Guid.NewGuid(), Guid.NewGuid(), "Movie");
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.NotNull(coordinator.GetSnapshot(room.RoomId));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(coordinator.GetSnapshot(room.RoomId));
    }

    [Fact]
    public void RejoinBeforeExpiryKeepsRoomAlive()
    {
        var clock = new FakeTimeProvider();
        using var coordinator = CreateCoordinator(clock);
        var room = coordinator.CreateRoom(Guid.NewGuid(), Guid.NewGuid(), "Movie");
        using (var first = coordinator.JoinRoom(room.RoomId, "Alex")!)
            first.Leave();
        clock.Advance(TimeSpan.FromMinutes(4));
        using var second = coordinator.JoinRoom(room.RoomId, "Alex");
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.NotNull(coordinator.GetSnapshot(room.RoomId));
    }

    private static InMemorySharedPlaybackCoordinator CreateCoordinator(TimeProvider? clock = null) =>
        new(Options.Create(new RoomsOptions { EmptyRoomExpiry = TimeSpan.FromMinutes(5) }), clock ?? TimeProvider.System);
}
