using Blockbuster.Core.SharedPlayback;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.SharedPlayback;

public sealed class InMemorySharedPlaybackCoordinator : ISharedPlaybackCoordinator, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _emptyRoomExpiry;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _cleanup;

    public InMemorySharedPlaybackCoordinator(IOptions<RoomsOptions> options, TimeProvider timeProvider)
    {
        _emptyRoomExpiry = options.Value.EmptyRoomExpiry;
        _timeProvider = timeProvider;
        _cleanup = timeProvider.CreateTimer(_ => RemoveExpired(), null, _emptyRoomExpiry, TimeSpan.FromMinutes(1));
    }

    public IReadOnlyList<SharedRoomSummary> ListRooms()
    {
        lock (_gate) return _rooms.Values.Select(ReadSummary)
            .OrderBy(room => room.MovieTitle, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SharedRoomSnapshot CreateRoom(Guid movieId, Guid mediaFileId, string movieTitle)
    {
        lock (_gate)
        {
            string id;
            do { id = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant(); }
            while (_rooms.ContainsKey(id));
            var room = new Room(id, movieId, mediaFileId, movieTitle, _timeProvider.GetUtcNow());
            _rooms.Add(id, room);
            return Snapshot(room);
        }
    }

    public SharedRoomSnapshot? GetSnapshot(string roomId)
    {
        lock (_gate) return _rooms.TryGetValue(roomId, out var room) ? Snapshot(room) : null;
    }

    public ISharedRoomSession? JoinRoom(string roomId, string profileName)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return null;
            var membershipId = Guid.NewGuid().ToString("N");
            room.Participants[membershipId] = profileName;
            room.EmptySince = null;
            return new Session(this, room.Id, membershipId, profileName);
        }
    }

    private void RemoveExpired()
    {
        lock (_gate)
        {
            var cutoff = _timeProvider.GetUtcNow() - _emptyRoomExpiry;
            foreach (var id in _rooms.Where(pair => pair.Value.Participants.Count == 0 && pair.Value.EmptySince <= cutoff)
                         .Select(pair => pair.Key).ToArray())
                _rooms.Remove(id);
        }
    }

    public void Dispose() => _cleanup.Dispose();

    private SharedRoomSnapshot? Apply(string roomId, string membershipId, string profileName, SharedPlaybackCommand command)
    {
        if (!double.IsFinite(command.PositionSeconds) || command.PositionSeconds < 0 || !double.IsFinite(command.PlaybackRate) || command.PlaybackRate is < .25 or > 4) return null;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room) || !room.Participants.ContainsKey(membershipId)) return null;
            room.IsPaused = command.IsPaused;
            room.AnchorPosition = command.PositionSeconds;
            room.AnchorTime = _timeProvider.GetUtcNow();
            room.Rate = command.PlaybackRate;
            room.LastController = profileName;
            room.Revision++;
            return Snapshot(room);
        }
    }

    private SharedRoomSnapshot? Leave(string roomId, string membershipId)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room) || !room.Participants.Remove(membershipId)) return null;
            if (room.Participants.Count == 0) room.EmptySince = _timeProvider.GetUtcNow();
            return Snapshot(room);
        }
    }

    private static SharedRoomSnapshot Snapshot(Room room) => new(room.Id, room.MovieId, room.MediaFileId, room.MovieTitle, room.IsPaused,
        room.AnchorPosition, room.AnchorTime, room.Rate, room.Revision, room.LastController,
        room.Participants.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList());

    private SharedRoomSummary ReadSummary(Room room)
    {
        var position = room.AnchorPosition + (room.IsPaused ? 0 : (_timeProvider.GetUtcNow() - room.AnchorTime).TotalSeconds * room.Rate);
        return new(room.Id, room.MovieId, room.MediaFileId, room.MovieTitle, room.Participants.Count, room.IsPaused,
            TimeSpan.FromSeconds(Math.Max(0, position)), room.Revision, room.LastController);
    }

    private sealed class Room(string id, Guid movieId, Guid mediaFileId, string movieTitle, DateTimeOffset now)
    {
        public string Id { get; } = id;
        public Guid MovieId { get; } = movieId;
        public Guid MediaFileId { get; } = mediaFileId;
        public string MovieTitle { get; } = movieTitle;
        public Dictionary<string, string> Participants { get; } = new(StringComparer.Ordinal);
        public bool IsPaused { get; set; } = true;
        public double AnchorPosition { get; set; }
        public DateTimeOffset AnchorTime { get; set; } = now;
        public double Rate { get; set; } = 1;
        public long Revision { get; set; }
        public string? LastController { get; set; }
        public DateTimeOffset? EmptySince { get; set; } = now;
    }

    private sealed class Session : ISharedRoomSession
    {
        private readonly InMemorySharedPlaybackCoordinator _owner;
        private readonly string _membershipId;
        private readonly string _profileName;
        private int _left;
        public Session(InMemorySharedPlaybackCoordinator owner, string roomId, string membershipId, string profileName)
        {
            _owner = owner;
            RoomId = roomId;
            _membershipId = membershipId;
            _profileName = profileName;
        }
        public string RoomId { get; }
        public SharedRoomSnapshot? Apply(SharedPlaybackCommand command) => Volatile.Read(ref _left) == 0 ? _owner.Apply(RoomId, _membershipId, _profileName, command) : null;
        public SharedRoomSnapshot? Leave() => Interlocked.Exchange(ref _left, 1) == 0 ? _owner.Leave(RoomId, _membershipId) : null;
        public void Dispose() => Leave();
    }
}
