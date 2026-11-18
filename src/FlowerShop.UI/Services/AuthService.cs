using FlowerShop.Shared;

namespace FlowerShop.Client.Services;

/// <summary>Holds the JWT (memory + localStorage) and tells the AuthenticationStateProvider when it changes.</summary>
public sealed class AuthService(BrowserStorage storage)
{
    const string Key = "fs.token";
    public string? Token { get; private set; }
    public event Action? Changed;

    public async Task InitAsync()
    {
        if (Token is not null) return;
        Token = await storage.GetAsync<string>(Key);
    }

    public async Task SignInAsync(AuthResponse r)
    {
        Token = r.Token;
        await storage.SetAsync(Key, r.Token);
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        Token = null;
        await storage.RemoveAsync(Key);
        Changed?.Invoke();
    }
}
