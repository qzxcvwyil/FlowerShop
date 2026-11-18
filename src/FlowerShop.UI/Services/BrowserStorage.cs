using System.Text.Json;
using Microsoft.JSInterop;

namespace FlowerShop.Client.Services;

/// <summary>localStorage wrapper. Never throws: private mode / blocked storage just means "no persistence".</summary>
public sealed class BrowserStorage(IJSRuntime js)
{
    public async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("fsStore.get", key);
            return raw is null ? default : JsonSerializer.Deserialize<T>(raw);
        }
        catch { return default; }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        try { await js.InvokeVoidAsync("fsStore.set", key, JsonSerializer.Serialize(value)); } catch { }
    }

    public async Task RemoveAsync(string key)
    {
        try { await js.InvokeVoidAsync("fsStore.remove", key); } catch { }
    }
}
