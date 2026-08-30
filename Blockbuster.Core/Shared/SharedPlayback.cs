namespace Blockbuster.Core.SharedPlayback;

public sealed record SharedRoomSummary(
    string RoomId, Guid MovieId, Guid MediaFileId, string MovieTitle,
    int ParticipantCount, bool IsPaused, TimeSpan Position, long Revision,
    string? LastControllingProfile);

public sealed record SharedRoomSnapshot(
    string RoomId, Guid MovieId, Guid MediaFileId, string MovieTitle,
    bool IsPaused, double AnchorPositionSeconds, DateTimeOffset ServerAnchorTime,
    double PlaybackRate, long Revision, string? LastControllingProfile,
    IReadOnlyList<string> Participants);

public sealed record SharedPlaybackCommand(
    bool IsPaused, double PositionSeconds, double PlaybackRate = 1);

public interface ISharedPlaybackCoordinator
{
    IReadOnlyList<SharedRoomSummary> ListRooms();
    SharedRoomSnapshot CreateRoom(Guid movieId, Guid mediaFileId, string movieTitle);
    SharedRoomSnapshot? GetSnapshot(string roomId);
    ISharedRoomSession? JoinRoom(string roomId, string profileName);
}

/// <summary>Connection-scoped authority to participate in one shared room.</summary>
public interface ISharedRoomSession : IDisposable
{
    string RoomId { get; }
    SharedRoomSnapshot? Apply(SharedPlaybackCommand command);
    SharedRoomSnapshot? Leave();
}
