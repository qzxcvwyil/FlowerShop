using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlowerShop.Shared;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>Pure functions: no server, no mocks (functional core).</summary>
public class OrderWorkflowTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Preparing, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Delivered, false)]
    [InlineData(OrderStatus.Pending, OrderStatus.OutForDelivery, false)]
    [InlineData(OrderStatus.Preparing, OrderStatus.OutForDelivery, true)]
    [InlineData(OrderStatus.Preparing, OrderStatus.Pending, false)]
    [InlineData(OrderStatus.OutForDelivery, OrderStatus.Delivered, true)]
    [InlineData(OrderStatus.OutForDelivery, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Pending, false)]
    [InlineData(OrderStatus.Pending, OrderStatus.Pending, true)]
    public void Transition_rules(OrderStatus from, OrderStatus to, bool allowed) => Assert.Equal(allowed, OrderWorkflow.CanMove(from, to));

    [Fact] public void Final_states_have_no_next_step()
    {
        Assert.True(OrderWorkflow.IsFinal(OrderStatus.Delivered));
        Assert.True(OrderWorkflow.IsFinal(OrderStatus.Cancelled));
        Assert.False(OrderWorkflow.IsFinal(OrderStatus.Pending));
        Assert.Empty(OrderWorkflow.Next(OrderStatus.Delivered));
    }

    [Fact] public void Every_open_state_can_reach_Delivered()
    {
        var s = OrderStatus.Pending;
        for (var i = 0; i < 3; i++) s = OrderWorkflow.Next(s)[0]; // follow the happy path
        Assert.Equal(OrderStatus.Delivered, s);
    }
}

public class ResultTests
{
    static Result<int> Half(int x) => x % 2 == 0 ? Result<int>.Ok(x / 2) : Result<int>.Fail("odd");

    [Fact] public void Map_transforms_success_and_keeps_failure()
    {
        Assert.Equal(4, Result<int>.Ok(2).Map(x => x * 2).Value);
        var failed = Result<int>.Fail("boom").Map(x => x * 2);
        Assert.False(failed.IsOk);
        Assert.Equal("boom", failed.Error);
    }

    [Fact] public void Bind_chains_steps_and_short_circuits_on_first_error()
    {
        Assert.Equal(2, Result<int>.Ok(8).Bind(x => Half(x)).Bind(x => Half(x)).Value);
        var r = Result<int>.Ok(6).Bind(x => Half(x)).Bind(x => Half(x)); // 6 -> 3 -> odd
        Assert.Equal("odd", r.Error);
    }

    [Fact] public void Match_returns_value_or_fallback()
    {
        Assert.Equal("v", Result<string>.Ok("v").Match(e => "fallback:" + e));
        Assert.Equal("fallback:x", Result<string>.Fail("x").Match(e => "fallback:" + e));
    }

    [Fact] public void Registration_with_missing_fields_fails_instead_of_throwing()
    {
        Assert.False(AuthValidator.Validate(new RegisterRequest(null!, "password1", "Name")).IsOk);
        Assert.False(AuthValidator.Validate(new RegisterRequest("a@b.kz", null!, "Name")).IsOk);
    }
}

public class OrderLifecycleApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
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

    async Task<ProductDto> Product(string prefix) =>
        (await factory.CreateClient().GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?pageSize=50"))!.Items.First(p => p.Name.StartsWith(prefix));

    async Task<OrderDto> Place(HttpClient customer, ProductDto p, int qty)
    {
        var res = await customer.PostAsJsonAsync("/api/orders", new CreateOrderRequest("Kabanbay Batyr 11, apt 7", null, [new(p.Id, qty)]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    [Fact] public async Task Skipping_a_step_in_the_workflow_returns_409()
    {
        var customer = await Login("flow1@test.kz", "Password1", "Flow One");
        var admin = await Login("admin@flowershop.kz", "Admin12345");
        var order = await Place(customer, await Product("Greeting Card"), 1);

        var res = await admin.PutAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest(OrderStatus.Delivered)); // Pending -> Delivered
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact] public async Task Cancelling_returns_items_to_stock_and_closes_the_order()
    {
        var customer = await Login("flow2@test.kz", "Password1", "Flow Two");
        var admin = await Login("admin@flowershop.kz", "Admin12345");
        var before = await Product("Sunflower");
        var order = await Place(customer, before, 3);
        Assert.Equal(before.Stock - 3, (await Product("Sunflower")).Stock);

        var cancel = await admin.PutAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest(OrderStatus.Cancelled));
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(before.Stock, (await Product("Sunflower")).Stock); // stock restored

        var again = await admin.PutAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateStatusRequest(OrderStatus.Preparing));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // final state
    }

    [Fact] public async Task Health_endpoint_is_ok_and_responses_carry_security_headers()
    {
        var c = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        var res = await c.GetAsync("/api/categories");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
    }
}
