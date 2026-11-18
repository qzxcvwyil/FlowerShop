using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;

namespace FlowerShop.Client.Services;

public sealed class JwtAuthStateProvider : AuthenticationStateProvider, IDisposable
{
    readonly AuthService _auth;
    static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public JwtAuthStateProvider(AuthService auth)
    {
        _auth = auth;
        _auth.Changed += OnChanged;
    }

    void OnChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    public void Dispose() => _auth.Changed -= OnChanged;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await _auth.InitAsync();
        return Parse(_auth.Token);
    }

    /// <summary>Pure function: JWT string -> authentication state. Unit-testable.</summary>
    public static AuthenticationState Parse(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return Anonymous;
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;
            if (root.TryGetProperty("exp", out var exp) && DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < DateTimeOffset.UtcNow)
                return Anonymous;
            var claims = root.EnumerateObject().Select(p => new Claim(p.Name, p.Value.ToString()));
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt", "name", "role")));
        }
        catch { return Anonymous; }
    }
}
