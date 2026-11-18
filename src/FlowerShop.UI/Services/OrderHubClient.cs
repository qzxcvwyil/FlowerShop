using FlowerShop.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace FlowerShop.Client.Services;

/// <summary>SignalR client with automatic reconnect; raises plain .NET events for components.</summary>
public sealed class OrderHubClient : IAsyncDisposable
{
    readonly NavigationManager nav;
    readonly AuthService auth;
    HubConnection? _hub;

    public OrderHubClient(NavigationManager nav, AuthService auth)
    {
        this.nav = nav; this.auth = auth;
        auth.Changed += () => _ = ResetAsync(); // new identity => new hub connection
    }

    async Task ResetAsync()
    {
        var h = _hub; _hub = null;
        if (h is not null) await h.DisposeAsync();
    }
    public event Action<int, OrderStatus>? StatusChanged;
    public event Action<OrderDto>? NewOrder;
    public bool Connected => _hub?.State == HubConnectionState.Connected;

    public async Task EnsureStartedAsync()
    {
        if (_hub is not null || auth.Token is null) return;
        _hub = new HubConnectionBuilder()
            .WithUrl(nav.ToAbsoluteUri("/hubs/orders"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(auth.Token))
            .WithAutomaticReconnect().Build();
        _hub.On<int, OrderStatus>("OrderStatusChanged", (id, s) => StatusChanged?.Invoke(id, s));
        _hub.On<OrderDto>("NewOrder", o => NewOrder?.Invoke(o));
        try { await _hub.StartAsync(); } catch { /* app still works without live updates */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
