using System.Collections.Concurrent;
using Blockbuster.Core.SharedPlayback;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.SharedPlayback;

public sealed class InMemorySharedPlaybackCoordinator : ISharedPlaybackCoordinator, IDisposable
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _emptyRoomExpiry;
    private readonly Timer _cleanup;

    public InMemorySharedPlaybackCoordinator(IOptions<RoomsOptions> options)
    {
        _emptyRoomExpiry = options.Value.EmptyRoomExpiry;
        _cleanup = new Timer(_ => RemoveExpired(), null, _emptyRoomExpiry, TimeSpan.FromMinutes(1));
    }

    public IReadOnlyList<SharedRoomSummary> ListRooms() => _rooms.Values
        .Select(room => room.ReadSummary()).OrderBy(room => room.MovieTitle, StringComparer.OrdinalIgnoreCase).ToList();

    public SharedRoomSnapshot CreateRoom(Guid movieId, Guid mediaFileId, string movieTitle)
    {
        Room room;
        do
        {
            var id = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant();
            room = new Room(id, movieId, mediaFileId, movieTitle);
        } while (!_rooms.TryAdd(room.Id, room));
        return room.ReadSnapshot();
    }

    public SharedRoomSnapshot? GetSnapshot(string roomId) =>
        _rooms.TryGetValue(roomId, out var room) ? room.ReadSnapshot() : null;

    public SharedRoomSnapshot? Join(string roomId, string connectionId, string profileName) =>
        _rooms.TryGetValue(roomId, out var room) ? room.Join(connectionId, profileName) : null;

    public SharedRoomSnapshot? Leave(string roomId, string connectionId) =>
        _rooms.TryGetValue(roomId, out var room) ? room.Leave(connectionId) : null;

    public SharedRoomSnapshot? Apply(string roomId, string connectionId, string profileName, SharedPlaybackCommand command)
    {
        if (!double.IsFinite(command.PositionSeconds) || command.PositionSeconds < 0
            || !double.IsFinite(command.PlaybackRate) || command.PlaybackRate is < .25 or > 4) return null;
        return _rooms.TryGetValue(roomId, out var room)
            ? room.Apply(connectionId, profileName, command) : null;
    }

    private void RemoveExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _emptyRoomExpiry;
        foreach (var pair in _rooms)
            if (pair.Value.IsEmptySince(cutoff)) _rooms.TryRemove(pair.Key, out _);
    }

    public void Dispose() => _cleanup.Dispose();

    private sealed class Room(string id, Guid movieId, Guid mediaFileId, string movieTitle)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _participants = new(StringComparer.Ordinal);
        private bool _isPaused = true;
        private double _anchorPosition;
        private DateTimeOffset _anchorTime = DateTimeOffset.UtcNow;
        private double _rate = 1;
        private long _revision;
        private string? _lastController;
        private DateTimeOffset? _emptySince = DateTimeOffset.UtcNow;
        public string Id { get; } = id;

        public SharedRoomSnapshot Join(string connectionId, string profileName)
        {
            lock (_gate) { _participants[connectionId] = profileName; _emptySince = null; return Snapshot(); }
        }

        public SharedRoomSnapshot Leave(string connectionId)
        {
            lock (_gate)
            {
                _participants.Remove(connectionId);
                if (_participants.Count == 0) _emptySince = DateTimeOffset.UtcNow;
                return Snapshot();
            }
        }

        public SharedRoomSnapshot Apply(string connectionId, string profileName, SharedPlaybackCommand command)
        {
            lock (_gate)
            {
                if (!_participants.ContainsKey(connectionId)) _participants[connectionId] = profileName;
                _isPaused = command.IsPaused;
                _anchorPosition = command.PositionSeconds;
                _anchorTime = DateTimeOffset.UtcNow;
                _rate = command.PlaybackRate;
                _lastController = profileName;
                _revision++;
                return Snapshot();
            }
        }

        public SharedRoomSnapshot ReadSnapshot() { lock (_gate) return Snapshot(); }
        public SharedRoomSummary ReadSummary()
        {
            lock (_gate)
            {
                var position = _anchorPosition + (_isPaused ? 0 : (DateTimeOffset.UtcNow - _anchorTime).TotalSeconds * _rate);
                return new(Id, movieId, mediaFileId, movieTitle, _participants.Count, _isPaused,
                    TimeSpan.FromSeconds(Math.Max(0, position)), _revision, _lastController);
            }
        }
        public bool IsEmptySince(DateTimeOffset cutoff) { lock (_gate) return _participants.Count == 0 && _emptySince <= cutoff; }
        private SharedRoomSnapshot Snapshot() => new(Id, movieId, mediaFileId, movieTitle, _isPaused,
            _anchorPosition, _anchorTime, _rate, _revision, _lastController,
            _participants.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList());
    }
}
