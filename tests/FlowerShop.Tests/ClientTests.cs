using System.Net;
using System.Net.Http.Json;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using FlowerShop.Client.Components;
using FlowerShop.Client.Layout;
using FlowerShop.Client.Pages;
using FlowerShop.Client.Services;
using FlowerShop.Client.State;
using FlowerShop.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowerShop.Tests;

// ---------- test infrastructure ----------
public sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Url, string? Auth, string? Body);
    public List<Call> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var body = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
        lock (Calls) Calls.Add(new Call(r.Method, r.RequestUri!.PathAndQuery, r.Headers.Authorization?.ToString(), body));
        return respond(r);
    }
}

public static class Http
{
    public static HttpResponseMessage Json(object? o, HttpStatusCode c = HttpStatusCode.OK) => new(c) { Content = JsonContent.Create(o) };
    public static HttpResponseMessage Problem(string detail, HttpStatusCode c) =>
        new(c) { Content = new StringContent($"{{\"detail\":\"{detail}\"}}", Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Empty(HttpStatusCode c) => new(c);

    public static ProductDto P(int id, string name = "Red Roses", decimal price = 18900, int stock = 5, string cat = "Bouquets") =>
        new(id, name, "desc", price, cat, "🌹", stock);
    public static PagedResult<ProductDto> Page(params ProductDto[] items) => new(items, items.Length, 1, 8);
    public static OrderDto Order(int id, OrderStatus s = OrderStatus.Pending) =>
        new(id, DateTime.UtcNow, s, "Kabanbay Batyr 11", "Ann", 0, 20000, [new OrderLineDto(1, "Red Roses", 1, 20000)]);
}

public sealed class ClientCtx : TestContext
{
    public FakeHandler Handler { get; }
    public ClientCtx(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        JSInterop.Mode = JSRuntimeMode.Loose; // localStorage calls are no-ops
        Handler = new FakeHandler(respond);
        Services.AddScoped(_ => new HttpClient(Handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") });
        Services.AddScoped<BrowserStorage>();
        Services.AddScoped<AuthService>();
        Services.AddScoped<ApiClient>();
        Services.AddScoped<CartState>();
        Services.AddScoped<ToastService>();
        Services.AddScoped<OrderHubClient>();
    }
    public CartState Cart => Services.GetRequiredService<CartState>();
    public AuthService Auth => Services.GetRequiredService<AuthService>();
    public ApiClient Api => Services.GetRequiredService<ApiClient>();
    public ToastService Toasts => Services.GetRequiredService<ToastService>();
    public FakeNavigationManager Nav => Services.GetRequiredService<FakeNavigationManager>();
}

// ---------- AuthService / auth state ----------
public class AuthServiceTests
{
    static string Jwt(string json) => $"h.{Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.s";

    [Fact]
    public async Task SignIn_sets_token_and_raises_Changed()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        var fired = 0; ctx.Auth.Changed += () => fired++;
        await ctx.Auth.SignInAsync(new AuthResponse("tok", "a@b.kz", "Ann", "Customer"));
        Assert.Equal("tok", ctx.Auth.Token); Assert.Equal(1, fired);
    }

    [Fact]
    public async Task SignOut_clears_token()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        await ctx.Auth.SignInAsync(new AuthResponse("tok", "a@b.kz", "Ann", "Customer"));
        await ctx.Auth.SignOutAsync();
        Assert.Null(ctx.Auth.Token);
    }

    [Fact]
    public async Task Init_restores_token_from_storage()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        ctx.JSInterop.Setup<string>("fsStore.get", "fs.token").SetResult("\"abc\"");
        await ctx.Auth.InitAsync();
        Assert.Equal("abc", ctx.Auth.Token);
    }

    [Fact]
    public async Task AuthState_provider_follows_sign_in_and_out()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        var provider = new JwtAuthStateProvider(ctx.Auth);
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        await ctx.Auth.SignInAsync(new AuthResponse(Jwt($"{{\"name\":\"Ann\",\"role\":\"Admin\",\"exp\":{exp}}}"), "a@b.kz", "Ann", "Admin"));
        Assert.True((await provider.GetAuthenticationStateAsync()).User.IsInRole("Admin"));
        await ctx.Auth.SignOutAsync();
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        provider.Dispose();
    }

    [Fact]
    public async Task Hub_client_without_token_does_not_connect()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        var hub = ctx.Services.GetRequiredService<OrderHubClient>();
        await hub.EnsureStartedAsync();
        Assert.False(hub.Connected);
        await hub.DisposeAsync();
    }
}

// ---------- ApiClient ----------
public class ApiClientTests
{
    [Fact]
    public async Task Successful_call_returns_parsed_value_and_sends_query()
    {
        using var ctx = new ClientCtx(_ => Http.Json(Http.Page(Http.P(1))));
        var r = await ctx.Api.ProductsAsync("Bouquets", "ro se", 2, 8);
        Assert.True(r.IsOk); Assert.Single(r.Value!.Items);
        Assert.Contains("page=2", ctx.Handler.Calls[0].Url);
        Assert.Contains("category=Bouquets", ctx.Handler.Calls[0].Url);
        Assert.Contains("search=ro%20se", ctx.Handler.Calls[0].Url);
    }

    [Fact]
    public async Task Bearer_header_is_sent_when_signed_in()
    {
        using var ctx = new ClientCtx(_ => Http.Json(new List<OrderDto>()));
        await ctx.Auth.SignInAsync(new AuthResponse("tok123", "a@b.kz", "Ann", "Customer"));
        await ctx.Api.MyOrdersAsync();
        Assert.Equal("Bearer tok123", ctx.Handler.Calls[0].Auth);
    }

    [Fact]
    public async Task ProblemDetails_message_is_surfaced()
    {
        using var ctx = new ClientCtx(_ => Http.Problem("Not enough 'Red Roses' in stock.", HttpStatusCode.Conflict));
        var r = await ctx.Api.CreateOrderAsync(new CreateOrderRequest("Long address 1", null, [new(1, 99)]));
        Assert.False(r.IsOk); Assert.Equal("Not enough 'Red Roses' in stock.", r.Error);
    }

    [Fact]
    public async Task Expired_token_signs_user_out()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.Unauthorized));
        await ctx.Auth.SignInAsync(new AuthResponse("old", "a@b.kz", "Ann", "Customer"));
        var r = await ctx.Api.MyOrdersAsync();
        Assert.Contains("expired", r.Error); Assert.Null(ctx.Auth.Token);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permission")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Status_codes_without_body_get_friendly_messages(HttpStatusCode code, string expected)
    {
        using var ctx = new ClientCtx(_ => Http.Empty(code));
        var r = await ctx.Api.AllOrdersAsync();
        Assert.Contains(expected, r.Error);
    }

    [Fact]
    public async Task Network_failure_becomes_result_error()
    {
        using var ctx = new ClientCtx(_ => throw new HttpRequestException("down"));
        var r = await ctx.Api.CategoriesAsync();
        Assert.Contains("Cannot reach the server", r.Error);
    }

    [Fact]
    public async Task Bad_json_becomes_result_error()
    {
        using var ctx = new ClientCtx(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json", Encoding.UTF8, "application/json") });
        var r = await ctx.Api.CategoriesAsync();
        Assert.Contains("Unexpected", r.Error);
    }

    [Fact]
    public async Task Cancelled_request_reports_cancelled()
    {
        using var ctx = new ClientCtx(_ => Http.Json(Http.Page()));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var r = await ctx.Api.ProductsAsync(null, null, 1, 8, cts.Token);
        Assert.Equal("cancelled", r.Error);
    }

    [Fact]
    public async Task Delete_with_no_content_is_ok()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.NoContent));
        Assert.True((await ctx.Api.DeleteProductAsync(1)).IsOk);
    }

    [Fact]
    public async Task Other_endpoints_use_expected_routes()
    {
        using var ctx = new ClientCtx(r => r.RequestUri!.AbsolutePath.Contains("auth")
            ? Http.Json(new AuthResponse("t", "a@b.kz", "Ann", "Customer")) : Http.Json(Http.Order(1)));
        await ctx.Api.LoginAsync(new LoginRequest("a@b.kz", "x"));
        await ctx.Api.RegisterAsync(new RegisterRequest("a@b.kz", "Password1", "Ann"));
        await ctx.Api.SetStatusAsync(1, OrderStatus.Preparing);
        await ctx.Api.CreateProductAsync(new ProductUpsert("n", "d", 1, "c", "e", 1));
        await ctx.Api.UpdateProductAsync(1, new ProductUpsert("n", "d", 1, "c", "e", 1));
        var urls = ctx.Handler.Calls.Select(c => $"{c.Method} {c.Url}").ToList();
        Assert.Contains("POST /api/auth/login", urls); Assert.Contains("POST /api/auth/register", urls);
        Assert.Contains("PUT /api/orders/1/status", urls); Assert.Contains("POST /api/products", urls); Assert.Contains("PUT /api/products/1", urls);
    }
}

// ---------- layout & small components ----------
public class LayoutTests
{
    [Fact]
    public void Anonymous_user_sees_sign_in_and_cart_badge_updates()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        ctx.AddTestAuthorization().SetNotAuthorized();
        var cut = ctx.RenderComponent<MainLayout>(p => p.Add(x => x.Body, "<p id=\"b\">hello</p>"));
        Assert.Contains("Sign in", cut.Markup); Assert.DoesNotContain("My orders", cut.Markup);
        Assert.Equal("0", cut.Find(".badge").TextContent);

        ctx.Cart.Add(Http.P(1));
        cut.WaitForAssertion(() => Assert.Equal("1", cut.Find(".badge").TextContent));
        Assert.Contains("hello", cut.Markup);
    }

    [Fact]
    public void Admin_sees_admin_links()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        var auth = ctx.AddTestAuthorization(); auth.SetAuthorized("Ann"); auth.SetRoles("Admin");
        var cut = ctx.RenderComponent<MainLayout>(p => p.Add(x => x.Body, "<p>x</p>"));
        Assert.Contains("My orders", cut.Markup); Assert.Contains("Products", cut.Markup); Assert.Contains("Ann", cut.Markup);
    }

    [Fact]
    public void Customer_does_not_see_admin_links()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        ctx.AddTestAuthorization().SetAuthorized("Bob");
        var cut = ctx.RenderComponent<MainLayout>(p => p.Add(x => x.Body, "<p>x</p>"));
        Assert.DoesNotContain("admin/products", cut.Markup);
    }

    [Fact]
    public void Sign_out_returns_to_shop()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        ctx.AddTestAuthorization().SetAuthorized("Ann");
        var cut = ctx.RenderComponent<MainLayout>(p => p.Add(x => x.Body, "<p>x</p>"));
        cut.Find("button.link").Click();
        cut.WaitForAssertion(() => Assert.Null(ctx.Auth.Token));
    }

    [Fact]
    public void ToastHost_shows_toasts()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        var cut = ctx.RenderComponent<ToastHost>();
        ctx.Toasts.Success("Saved!"); ctx.Toasts.Error("Oops");
        cut.WaitForAssertion(() => { Assert.Contains("Saved!", cut.Markup); Assert.Contains("Oops", cut.Markup); });
        Assert.Contains("success", cut.Markup); Assert.Contains("error", cut.Markup);
    }

    [Fact]
    public void OrderCard_renders_lines_total_and_child_content()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        var cut = ctx.RenderComponent<OrderCard>(p => p.Add(x => x.Order, Http.Order(7, OrderStatus.Delivered)).AddChildContent("<b id=\"extra\">extra</b>"));
        Assert.Contains("Order #7", cut.Markup); Assert.Contains("Red Roses", cut.Markup);
        Assert.Contains("Delivered", cut.Markup); Assert.NotNull(cut.Find("#extra"));
    }
}

// ---------- pages ----------
public class CatalogPageTests
{
    static HttpResponseMessage Route(HttpRequestMessage r) =>
        r.RequestUri!.AbsolutePath.EndsWith("categories") ? Http.Json(new List<string> { "Bouquets", "Plants" })
        : Http.Json(Http.Page(Http.P(1), Http.P(2, "Orchid", 16500, 3, "Plants")));

    [Fact]
    public void Lists_products_and_adds_to_cart()
    {
        using var ctx = new ClientCtx(Route);
        var cut = ctx.RenderComponent<Catalog>(p => p.AddCascadingValue(ctx.Cart));
        cut.WaitForAssertion(() => Assert.Contains("Orchid", cut.Markup));
        Assert.Contains("Page 1 of 1", cut.Markup);
        cut.FindAll(".product")[0].QuerySelector("button")!.Click();
        Assert.Equal(1, ctx.Cart.Count);
    }

    [Fact]
    public void Search_sends_debounced_request()
    {
        using var ctx = new ClientCtx(Route);
        var cut = ctx.RenderComponent<Catalog>(p => p.AddCascadingValue(ctx.Cart));
        cut.WaitForAssertion(() => Assert.Contains("Orchid", cut.Markup));
        cut.Find("input[type=search]").Input("rose");
        cut.WaitForAssertion(() => Assert.Contains(ctx.Handler.Calls, c => c.Url.Contains("search=rose")), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Category_filter_sends_category()
    {
        using var ctx = new ClientCtx(Route);
        var cut = ctx.RenderComponent<Catalog>(p => p.AddCascadingValue(ctx.Cart));
        cut.WaitForAssertion(() => Assert.Contains("Orchid", cut.Markup));
        cut.Find("select").Change("Plants");
        cut.WaitForAssertion(() => Assert.Contains(ctx.Handler.Calls, c => c.Url.Contains("category=Plants")));
    }

    [Fact]
    public void Server_error_shows_retry()
    {
        using var ctx = new ClientCtx(r => r.RequestUri!.AbsolutePath.EndsWith("categories") ? Http.Json(new List<string>()) : Http.Problem("Boom", HttpStatusCode.InternalServerError));
        var cut = ctx.RenderComponent<Catalog>(p => p.AddCascadingValue(ctx.Cart));
        cut.WaitForAssertion(() => { Assert.Contains("Boom", cut.Markup); Assert.Contains("Retry", cut.Markup); });
    }

    [Fact]
    public void Empty_result_shows_message()
    {
        using var ctx = new ClientCtx(r => r.RequestUri!.AbsolutePath.EndsWith("categories") ? Http.Json(new List<string>()) : Http.Json(Http.Page()));
        var cut = ctx.RenderComponent<Catalog>(p => p.AddCascadingValue(ctx.Cart));
        cut.WaitForAssertion(() => Assert.Contains("No flowers match", cut.Markup));
    }
}

public class CartPageTests
{
    [Fact]
    public void Empty_cart_message()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        var cut = ctx.RenderComponent<CartPage>(p => p.AddCascadingValue(ctx.Cart));
        Assert.Contains("Your cart is empty", cut.Markup);
    }

    [Fact]
    public void Quantity_buttons_and_remove_update_totals()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        ctx.Cart.Add(Http.P(1, price: 5000));
        var cut = ctx.RenderComponent<CartPage>(p => p.AddCascadingValue(ctx.Cart));
        Assert.Contains("6 500", cut.Markup.Replace('\u00a0', ' ')); // 5000 + 1500 delivery
        cut.Find("button[aria-label='Increase']").Click();
        Assert.Equal(2, ctx.Cart.Count);
        cut.Find("button[aria-label='Decrease']").Click();
        Assert.Equal(1, ctx.Cart.Count);
        cut.Find("button[aria-label^='Remove']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Your cart is empty", cut.Markup));
    }
}

public class LoginPageTests
{
    [Fact]
    public void Login_success_stores_token_and_navigates()
    {
        using var ctx = new ClientCtx(_ => Http.Json(new AuthResponse("tok", "a@b.kz", "Ann", "Customer")));
        var cut = ctx.RenderComponent<Login>();
        cut.Find("input[type=email]").Change("a@b.kz");
        cut.Find("input[type=password]").Change("Password1");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal("tok", ctx.Auth.Token));
    }

    [Fact]
    public void Login_failure_shows_error()
    {
        using var ctx = new ClientCtx(_ => Http.Problem("Invalid email or password.", HttpStatusCode.Unauthorized));
        var cut = ctx.RenderComponent<Login>();
        cut.Find("input[type=email]").Change("a@b.kz");
        cut.Find("input[type=password]").Change("wrong");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("Invalid email or password.", cut.Markup));
        Assert.Null(ctx.Auth.Token);
    }

    [Fact]
    public void Toggle_to_register_and_submit_calls_register_endpoint()
    {
        using var ctx = new ClientCtx(_ => Http.Json(new AuthResponse("tok", "n@b.kz", "New", "Customer")));
        var cut = ctx.RenderComponent<Login>();
        cut.Find("button.link").Click();
        Assert.Contains("Full name", cut.Markup);
        cut.Find("input").Change("New User");
        cut.Find("input[type=email]").Change("n@b.kz");
        cut.Find("input[type=password]").Change("Password1");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains(ctx.Handler.Calls, c => c.Url == "/api/auth/register"));
    }
}

public class CheckoutPageTests
{
    [Fact]
    public void Empty_cart_shows_message()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.OK));
        var cut = ctx.RenderComponent<Checkout>(p => p.AddCascadingValue(ctx.Cart));
        Assert.Contains("Your cart is empty", cut.Markup);
    }

    [Fact]
    public void Short_address_is_rejected_client_side()
    {
        using var ctx = new ClientCtx(_ => Http.Json(Http.Order(1), HttpStatusCode.Created));
        ctx.Cart.Add(Http.P(1));
        var cut = ctx.RenderComponent<Checkout>(p => p.AddCascadingValue(ctx.Cart));
        cut.Find("input").Change("abc");
        cut.Find("form").Submit();
        Assert.Contains("full address", cut.Markup);
        Assert.DoesNotContain(ctx.Handler.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public void Valid_order_is_posted_cart_cleared_and_user_redirected()
    {
        using var ctx = new ClientCtx(_ => Http.Json(Http.Order(5), HttpStatusCode.Created));
        ctx.Cart.Add(Http.P(1), 2);
        var cut = ctx.RenderComponent<Checkout>(p => p.AddCascadingValue(ctx.Cart));
        cut.Find("input").Change("Kabanbay Batyr 11, apt 7");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(0, ctx.Cart.Count));
        Assert.Contains(ctx.Handler.Calls, c => c.Method == HttpMethod.Post && c.Url == "/api/orders" && c.Body!.Contains("\"quantity\":2"));
        Assert.EndsWith("/orders", ctx.Nav.Uri);
    }

    [Fact]
    public void Server_rejection_is_shown_and_cart_kept()
    {
        using var ctx = new ClientCtx(_ => Http.Problem("Not enough 'Red Roses' in stock.", HttpStatusCode.Conflict));
        ctx.Cart.Add(Http.P(1));
        var cut = ctx.RenderComponent<Checkout>(p => p.AddCascadingValue(ctx.Cart));
        cut.Find("input").Change("Kabanbay Batyr 11, apt 7");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("Not enough", cut.Markup));
        Assert.Equal(1, ctx.Cart.Count);
    }
}

public class OrdersPageTests
{
    [Fact]
    public void Lists_my_orders()
    {
        using var ctx = new ClientCtx(_ => Http.Json(new List<OrderDto> { Http.Order(1), Http.Order(2, OrderStatus.Preparing) }));
        var cut = ctx.RenderComponent<Orders>();
        cut.WaitForAssertion(() => { Assert.Contains("Order #1", cut.Markup); Assert.Contains("Order #2", cut.Markup); });
        Assert.Contains("Preparing", cut.Markup);
    }

    [Fact]
    public void Empty_state()
    {
        using var ctx = new ClientCtx(_ => Http.Json(new List<OrderDto>()));
        var cut = ctx.RenderComponent<Orders>();
        cut.WaitForAssertion(() => Assert.Contains("No orders yet", cut.Markup));
    }

    [Fact]
    public void Error_state_has_retry()
    {
        using var ctx = new ClientCtx(_ => Http.Empty(HttpStatusCode.InternalServerError));
        var cut = ctx.RenderComponent<Orders>();
        cut.WaitForAssertion(() => Assert.Contains("Retry", cut.Markup));
    }
}

public class AdminProductsPageTests
{
    static HttpResponseMessage Route(HttpRequestMessage r) =>
        r.Method == HttpMethod.Delete ? Http.Empty(HttpStatusCode.NoContent)
        : r.Method == HttpMethod.Post ? Http.Json(Http.P(9, "Tulip"), HttpStatusCode.Created)
        : r.Method == HttpMethod.Put ? Http.Json(Http.P(1))
        : Http.Json(Http.Page(Http.P(1), Http.P(2, "Orchid", 16500, 3, "Plants")));

    [Fact]
    public void Lists_products()
    {
        using var ctx = new ClientCtx(Route);
        var cut = ctx.RenderComponent<AdminProducts>();
        cut.WaitForAssertion(() => { Assert.Contains("Red Roses", cut.Markup); Assert.Contains("Orchid", cut.Markup); });
    }

    [Fact]
    public void Add_product_posts_to_api()
    {
        using var ctx = new ClientCtx(Route);
        var cut = ctx.RenderComponent<AdminProducts>();
        cut.WaitForAssertion(() => Assert.Contains("Orchid", cut.Markup));
        // Re-query before every change: each Change() re-renders the form and invalidates previously found elements (bUnit).
        cut.FindAll("input")[0].Change("Tulip");        // Name
        cut.FindAll("input")[4].Change("Bouquets");     // Category
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains(ctx.Handler.Calls, c => c.Method == HttpMethod.Post && c.Url == "/api/products" && c.Body!.Contains("Tulip")));
    }

    [Fact]
    public void Edit_then_save_puts_to_api()
    {
        using var ctx = new ClientCtx(Route);
        var cut = ctx.RenderComponent<AdminProducts>();
        cut.WaitForAssertion(() => Assert.Contains("Orchid", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Assert.Contains("Edit product #1", cut.Markup);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains(ctx.Handler.Calls, c => c.Method == HttpMethod.Put && c.Url == "/api/products/1"));
    }

    [Fact]
    public void Delete_requires_confirmation()
    {
        using var ctx = new ClientCtx(Route);
        var cut = ctx.RenderComponent<AdminProducts>();
        cut.WaitForAssertion(() => Assert.Contains("Orchid", cut.Markup));
        cut.Find("button[aria-label='Delete Red Roses']").Click();
        Assert.DoesNotContain(ctx.Handler.Calls, c => c.Method == HttpMethod.Delete);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirm delete").Click();
        cut.WaitForAssertion(() => Assert.Contains(ctx.Handler.Calls, c => c.Method == HttpMethod.Delete && c.Url == "/api/products/1"));
    }
}