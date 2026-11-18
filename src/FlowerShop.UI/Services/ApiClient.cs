using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowerShop.Shared;

namespace FlowerShop.Client.Services;

/// <summary>Typed API wrapper. Every call returns Result&lt;T&gt; so pages never deal with exceptions.</summary>
public sealed class ApiClient(HttpClient http, AuthService auth)
{
    public Task<Result<PagedResult<ProductDto>>> ProductsAsync(string? category, string? search, int page, int size, CancellationToken ct = default) =>
        Send<PagedResult<ProductDto>>(HttpMethod.Get, $"api/products?page={page}&pageSize={size}&category={Uri.EscapeDataString(category ?? "")}&search={Uri.EscapeDataString(search ?? "")}", null, ct);
    public Task<Result<List<string>>> CategoriesAsync() => Send<List<string>>(HttpMethod.Get, "api/categories", null);
    public Task<Result<AuthResponse>> LoginAsync(LoginRequest r) => Send<AuthResponse>(HttpMethod.Post, "api/auth/login", r);
    public Task<Result<AuthResponse>> RegisterAsync(RegisterRequest r) => Send<AuthResponse>(HttpMethod.Post, "api/auth/register", r);
    public Task<Result<OrderDto>> CreateOrderAsync(CreateOrderRequest r) => Send<OrderDto>(HttpMethod.Post, "api/orders", r);
    public Task<Result<List<OrderDto>>> MyOrdersAsync() => Send<List<OrderDto>>(HttpMethod.Get, "api/orders/mine", null);
    public Task<Result<List<OrderDto>>> AllOrdersAsync() => Send<List<OrderDto>>(HttpMethod.Get, "api/orders", null);
    public Task<Result<OrderDto>> SetStatusAsync(int id, OrderStatus s) => Send<OrderDto>(HttpMethod.Put, $"api/orders/{id}/status", new UpdateStatusRequest(s));

    public Task<Result<ProductDto>> CreateProductAsync(ProductUpsert p) => Send<ProductDto>(HttpMethod.Post, "api/products", p);
    public Task<Result<ProductDto>> UpdateProductAsync(int id, ProductUpsert p) => Send<ProductDto>(HttpMethod.Put, $"api/products/{id}", p);
    public Task<Result<bool>> DeleteProductAsync(int id) => Send<bool>(HttpMethod.Delete, $"api/products/{id}", null);

    async Task<Result<T>> Send<T>(HttpMethod method, string url, object? body, CancellationToken ct = default)
    {
        try
        {
            await auth.InitAsync();
            using var req = new HttpRequestMessage(method, url);
            if (auth.Token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NoContent) return Result<T>.Ok(default!);
            if (res.IsSuccessStatusCode)
                return Result<T>.Ok((await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized && auth.Token is not null)
            {
                await auth.SignOutAsync(); // expired token
                return Result<T>.Fail("Your session expired. Please sign in again.");
            }
            return Result<T>.Fail(await ReadError(res));
        }
        catch (OperationCanceledException) { return Result<T>.Fail("cancelled"); }
        catch (HttpRequestException) { return Result<T>.Fail("Cannot reach the server. Check your connection and retry."); }
        catch (JsonException) { return Result<T>.Fail("Unexpected server response."); }
    }

    static async Task<string> ReadError(HttpResponseMessage res)
    {
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.GetString() is { Length: > 0 } s) return s;
        }
        catch { }
        return res.StatusCode switch
        {
            System.Net.HttpStatusCode.Forbidden => "You don't have permission to do that.",
            System.Net.HttpStatusCode.TooManyRequests => "Too many attempts. Please wait a minute.",
            _ => $"Request failed ({(int)res.StatusCode})."
        };
    }
}
