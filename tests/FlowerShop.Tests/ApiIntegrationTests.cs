using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FlowerShop.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    readonly string _db = Path.Combine(Path.GetTempPath(), $"fs-test-{Guid.NewGuid():N}.db");
    protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("ConnectionStrings:Default", $"Data Source={_db}");
    protected override void Dispose(bool disposing) { base.Dispose(disposing); try { File.Delete(_db); } catch { } }
}

public class ApiIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    async Task<HttpClient> Login(string email, string password, string? name = null)
    {
        var c = factory.CreateClient();
        var res = name is null
            ? await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password))
            : await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, password, name));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return c;
    }

    async Task<List<ProductDto>> Products() =>
        (await factory.CreateClient().GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?pageSize=50"))!.Items.ToList();

    [Fact] public async Task Catalog_is_public_paged_and_searchable()
    {
        var c = factory.CreateClient();
        var page = await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?pageSize=5");
        Assert.Equal(5, page!.Items.Count); Assert.True(page.Total >= 12);
        var found = await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?search=rose");
        Assert.All(found!.Items, p => Assert.Contains("Rose", p.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public async Task Ordering_requires_authentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/orders", new CreateOrderRequest("Some long address", null, [new(1, 1)]))).StatusCode);

    [Fact] public async Task Customer_cannot_open_admin_endpoints()
    {
        var c = await Login("user1@test.kz", "Password1", "User One");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/products", new ProductUpsert("x", "", 1, "c", "🌹", 1))).StatusCode);
    }

    [Fact] public async Task Wrong_password_is_rejected()
    {
        await Login("user2@test.kz", "Password1", "User Two");
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("user2@test.kz", "WrongPass9"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact] public async Task Full_order_flow_with_server_side_pricing_and_stock()
    {
        var customer = await Login("buyer@test.kz", "Password1", "Buyer");
        var admin = await Login("admin@flowershop.kz", "Admin12345");
        var tulips = (await Products()).First(p => p.Name.StartsWith("Spring Tulips"));

        var res = await customer.PostAsJsonAsync("/api/orders", new CreateOrderRequest("Kabanbay Batyr 11, apt 7", "Call me", [new(tulips.Id, 2)]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var order = (await res.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(2 * tulips.Price, order.Total); // 29 000 -> free delivery
        Assert.Equal(0, order.Delivery);

        var after = (await Products()).First(p => p.Id == tulips.Id);
        Assert.Equal(tulips.Stock - 2, after.Stock); // stock decremented (cache invalidated)

        var upd = await admin.PutAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest(OrderStatus.Preparing));
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        var mine = await customer.GetFromJsonAsync<List<OrderDto>>("/api/orders/mine");
        Assert.Equal(OrderStatus.Preparing, mine!.Single(o => o.Id == order.Id).Status);
    }

    [Fact] public async Task Ordering_more_than_stock_returns_409()
    {
        var c = await Login("buyer2@test.kz", "Password1", "Buyer Two");
        var arch = (await Products()).First(p => p.Name.StartsWith("Wedding"));
        var res = await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest("Some long address 1", null, [new(arch.Id, arch.Stock + 1)]));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact] public async Task Invalid_order_returns_problem_details_400()
    {
        var c = await Login("buyer3@test.kz", "Password1", "Buyer Three");
        var res = await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest("x", null, []));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
