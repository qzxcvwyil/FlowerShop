using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FlowerShop.Server;

/// <summary>Real-time channel: customers get their own order updates, admins get new orders.</summary>
[Authorize]
public sealed class OrderHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var id = Context.User!.FindFirst("sub")!.Value;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{id}");
        if (Context.User.IsInRole("Admin")) await Groups.AddToGroupAsync(Context.ConnectionId, "admins");
        await base.OnConnectedAsync();
    }
}
