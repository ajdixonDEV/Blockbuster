using Blockbuster.Core.SharedPlayback;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.SharedPlayback;
using Microsoft.Extensions.Options;
using Xunit;

namespace Blockbuster.Tests.SharedPlayback;

public sealed class SharedPlaybackCoordinatorTests
{
    [Fact]
    public void CommandsAreSerializedAndLastAcceptedStateIsAuthoritative()
    {
        using var coordinator = CreateCoordinator();
        var room = coordinator.CreateRoom(Guid.NewGuid(), Guid.NewGuid(), "Arrival");
        coordinator.Join(room.RoomId, "first-connection", "Alex");
        coordinator.Join(room.RoomId, "second-connection", "Sam");

        var first = coordinator.Apply(room.RoomId, "first-connection", "Alex", new(false, 42));
        var second = coordinator.Apply(room.RoomId, "second-connection", "Sam", new(true, 57));

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
        coordinator.Join(room.RoomId, "creator", "Alex");
        var expected = coordinator.Apply(room.RoomId, "creator", "Alex", new(false, 20));
        coordinator.Leave(room.RoomId, "creator");

        var rejoined = coordinator.Join(room.RoomId, "reconnected", "Alex");

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
        coordinator.Join(room.RoomId, "connection", "Viewer");

        Assert.Null(coordinator.Apply(room.RoomId, "connection", "Viewer", new(false, double.NaN)));
        Assert.Equal(0, coordinator.GetSnapshot(room.RoomId)!.Revision);
    }

    private static InMemorySharedPlaybackCoordinator CreateCoordinator() =>
        new(Options.Create(new RoomsOptions { EmptyRoomExpiry = TimeSpan.FromMinutes(5) }));
}
