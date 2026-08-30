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
        var snapshot = coordinator.Join(roomId, Context.ConnectionId, profile)
            ?? throw new HubException("The shared room no longer exists.");
        Context.Items["room"] = roomId;
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("RoomUpdated", snapshot);
        return snapshot;
    }

    public async Task SendCommand(string roomId, SharedPlaybackCommand command)
    {
        var profile = Context.User?.FindFirstValue(ClaimTypes.Name) ?? "Viewer";
        var snapshot = coordinator.Apply(roomId, Context.ConnectionId, profile, command)
            ?? throw new HubException("The command or room was invalid.");
        await Clients.Group(roomId).SendAsync("StateChanged", snapshot);
    }

    public Task<SharedRoomSnapshot> GetSnapshot(string roomId) =>
        Task.FromResult(coordinator.GetSnapshot(roomId) ?? throw new HubException("The shared room no longer exists."));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("room", out var value) && value is string roomId)
        {
            var snapshot = coordinator.Leave(roomId, Context.ConnectionId);
            if (snapshot is not null) await Clients.Group(roomId).SendAsync("RoomUpdated", snapshot);
        }
        await base.OnDisconnectedAsync(exception);
    }
}
