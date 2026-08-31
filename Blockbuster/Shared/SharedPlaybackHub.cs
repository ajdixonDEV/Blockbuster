using System.Security.Claims;
using Blockbuster.Core.SharedPlayback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Blockbuster.SharedPlayback;

[Authorize]
public sealed class SharedPlaybackHub(ISharedPlaybackCoordinator coordinator) : Hub
{
    public async Task<SharedRoomSnapshot> JoinRoom(string roomId)
    {
        var profile = Context.User?.FindFirstValue(ClaimTypes.Name) ?? "Viewer";
        var session = coordinator.JoinRoom(roomId, profile)
            ?? throw new HubException("The shared room no longer exists.");
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, session.RoomId);
            Context.Items["room-session"] = session;
            var snapshot = coordinator.GetSnapshot(session.RoomId)!;
            await Clients.Group(session.RoomId).SendAsync("RoomUpdated", snapshot);
            return snapshot;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task SendCommand(string roomId, SharedPlaybackCommand command)
    {
        if (!Context.Items.TryGetValue("room-session", out var value) || value is not ISharedRoomSession session || !string.Equals(session.RoomId, roomId, StringComparison.OrdinalIgnoreCase))
            throw new HubException("Join the room before sending playback commands.");
        var snapshot = session.Apply(command)
            ?? throw new HubException("The command or room was invalid.");
        await Clients.Group(roomId).SendAsync("StateChanged", snapshot);
    }

    public async Task SetBuffering(string roomId, bool isBuffering, double positionSeconds)
    {
        if (!Context.Items.TryGetValue("room-session", out var value) || value is not ISharedRoomSession session || !string.Equals(session.RoomId, roomId, StringComparison.OrdinalIgnoreCase))
            throw new HubException("Join the room before reporting buffering.");
        var snapshot = session.SetBuffering(isBuffering, positionSeconds)
            ?? throw new HubException("The buffering state or room was invalid.");
        await Clients.Group(roomId).SendAsync("StateChanged", snapshot);
    }

    public Task<SharedRoomSnapshot> GetSnapshot(string roomId) =>
        Task.FromResult(coordinator.GetSnapshot(roomId) ?? throw new HubException("The shared room no longer exists."));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("room-session", out var value) && value is ISharedRoomSession session)
        {
            var snapshot = session.Leave();
            if (snapshot is not null)
                await Clients.Group(session.RoomId).SendAsync("RoomUpdated", snapshot);
        }
        await base.OnDisconnectedAsync(exception);
    }
}
