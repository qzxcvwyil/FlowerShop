using Microsoft.Playwright;
using Xunit;

namespace FlowerShop.E2E;

/// <summary>Shared Playwright instance; browsers (chromium/firefox/webkit) are launched on demand.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    public static string BaseUrl => (Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:5000").TrimEnd('/');
    public static string AdminEmail => Environment.GetEnvironmentVariable("ADMIN_EMAIL") ?? "admin@flowershop.kz";
    public static string AdminPassword => Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "Admin12345";

    /// <summary>Playwright's WebKit build for Windows (WinCairo) cannot start Blazor WebAssembly, so the page never renders.
    /// On Windows WebKit runs are skipped (devices fall back to Chromium emulation). Set FORCE_WEBKIT=1 to try anyway.</summary>
    public static bool WebKitUnsupported => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("FORCE_WEBKIT") != "1";

    public bool ServerUp { get; private set; }
    public IPlaywright? Pw { get; private set; }
    readonly Dictionary<string, IBrowser> _browsers = new();

    public async Task InitializeAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            ServerUp = (await http.GetAsync(BaseUrl + "/api/categories")).IsSuccessStatusCode;
        }
        catch { ServerUp = false; }
        if (ServerUp) Pw = await Playwright.CreateAsync();
    }

    public async Task<IBrowser> BrowserAsync(string name)
    {
        if (_browsers.TryGetValue(name, out var existing)) return existing;
        IBrowserType type = name switch { "firefox" => Pw!.Firefox, "webkit" => Pw!.Webkit, _ => Pw!.Chromium };
        var b = await type.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
        return _browsers[name] = b;
    }

    public async Task DisposeAsync()
    {
        foreach (var b in _browsers.Values) await b.CloseAsync();
        Pw?.Dispose();
    }
}

[CollectionDefinition("browser")]
public class BrowserCollection : ICollectionFixture<BrowserFixture> { }

[Collection("browser")]
public class E2ETests(BrowserFixture fx)
{
    const string NoServer = "Start the server first: dotnet run --project src/FlowerShop.Server --environment Development";

    async Task<IPage> NewPageAsync(string browser = "chromium", string? device = null)
    {
        if (browser == "webkit" && BrowserFixture.WebKitUnsupported)
        {
            Skip.If(device is null, "WebKit (Safari engine) cannot run Blazor WebAssembly on Windows; run this test on Linux/macOS/WSL/CI.");
            browser = "chromium"; // device emulation (viewport, touch, user agent) still checks the layout
        }
        var b = await fx.BrowserAsync(browser);
        var opts = device is null ? new BrowserNewContextOptions() : new BrowserNewContextOptions(fx.Pw!.Devices[device]);
        var page = await (await b.NewContextAsync(opts)).NewPageAsync();
        page.SetDefaultTimeout(30_000); // Blazor WASM first load can be slow
        return page;
    }

    static async Task OpenLoginAsync(IPage page)
    {
        await page.GotoAsync(BrowserFixture.BaseUrl + "/login");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" }).WaitForAsync(); // Blazor has rendered
    }

    static async Task RegisterAsync(IPage page, string email)
    {
        await OpenLoginAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create a new account" }).ClickAsync();
        await page.GetByLabel("Full name").FillAsync("E2E Customer");
        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password").FillAsync("Password1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Register" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "My orders" }).WaitForAsync(); // signed in
    }

    static async Task PlaceOrderAsync(IPage page)
    {
        await page.Locator(".product").First.WaitForAsync();
        await page.Locator(".product").First.GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
        await page.GetByText("added to cart").WaitForAsync();
        await page.Locator("a.cart").ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Checkout" }).ClickAsync();
        await page.GetByLabel("Delivery address").FillAsync("Kabanbay Batyr 11, apt 7");
        await page.GetByRole(AriaRole.Button, new() { Name = "Place order" }).ClickAsync();
        await page.Locator(".order").First.WaitForAsync(); // landed on "My orders" with the new order
    }

    static string NewEmail() => $"e2e{Guid.NewGuid():N}@test.kz";

    // ---------- cross-browser: same flows in Chromium, Firefox and WebKit (Safari engine) ----------
    [SkippableTheory]
    [InlineData("chromium")] [InlineData("firefox")] [InlineData("webkit")]
    public async Task Catalog_loads_and_search_filters_products(string browser)
    {
        Skip.IfNot(fx.ServerUp, NoServer);
        var page = await NewPageAsync(browser);
        await page.GotoAsync(BrowserFixture.BaseUrl + "/");
        await page.Locator(".product").First.WaitForAsync();
        Assert.True(await page.Locator(".product").CountAsync() > 1);

        await page.Locator("input[type=search]").FillAsync("rose");
        await page.WaitForFunctionAsync("document.querySelectorAll('.product').length === 1");
        Assert.Contains("Red Roses", await page.Locator(".product").First.InnerTextAsync());
    }

    [SkippableTheory]
    [InlineData("chromium")] [InlineData("firefox")] [InlineData("webkit")]
    public async Task Customer_can_register_add_to_cart_and_place_order(string browser)
    {
        Skip.IfNot(fx.ServerUp, NoServer);
        var page = await NewPageAsync(browser);
        await RegisterAsync(page, NewEmail());
        await PlaceOrderAsync(page);
        Assert.Contains("Pending", await page.Locator(".order").First.InnerTextAsync());
    }

    // ---------- cross-device: phones and tablet emulation ----------
    [SkippableTheory]
    [InlineData("iPhone 13", "webkit")]
    [InlineData("Pixel 7", "chromium")]
    [InlineData("iPad Pro 11", "webkit")]
    [InlineData("Desktop Chrome", "chromium")]
    [InlineData("Desktop Firefox", "firefox")]
    [InlineData("Desktop Safari", "webkit")]
    public async Task Layout_fits_the_screen_and_core_flow_works_on_device(string device, string browser)
    {
        Skip.IfNot(fx.ServerUp, NoServer);
        var page = await NewPageAsync(browser, device);
        const string noOverflow = "document.documentElement.scrollWidth <= window.innerWidth + 1";

        await page.GotoAsync(BrowserFixture.BaseUrl + "/");
        await page.Locator(".product").First.WaitForAsync();
        Assert.True(await page.EvaluateAsync<bool>(noOverflow), $"horizontal scroll on catalog ({device})");
        Assert.True(await page.Locator("a.cart").IsVisibleAsync());

        await page.Locator(".product").First.GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
        await page.Locator("a.cart").ClickAsync();
        await page.GetByText("Subtotal").WaitForAsync();
        Assert.True(await page.EvaluateAsync<bool>(noOverflow), $"horizontal scroll on cart ({device})");

        await OpenLoginAsync(page);
        Assert.True(await page.EvaluateAsync<bool>(noOverflow), $"horizontal scroll on login ({device})");
    }

    // ---------- security & real-time ----------
    [SkippableFact]
    public async Task Customer_cannot_open_admin_area()
    {
        Skip.IfNot(fx.ServerUp, NoServer);
        var page = await NewPageAsync();
        await RegisterAsync(page, NewEmail());
        await page.GotoAsync(BrowserFixture.BaseUrl + "/admin");
        await page.GetByText("Access denied").WaitForAsync();
    }

    static async Task LoginAsAdminAsync(IPage admin)
    {
        await OpenLoginAsync(admin);
        await admin.GetByLabel("Email").FillAsync(BrowserFixture.AdminEmail);
        await admin.GetByLabel("Password").FillAsync(BrowserFixture.AdminPassword);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await admin.GetByRole(AriaRole.Link, new() { Name = "Products" }).WaitForAsync(); // admin menu visible
    }

    [SkippableFact]
    public async Task Admin_sees_new_order_live_without_reloading_and_admin_assembly_is_lazy_loaded()
    {
        Skip.IfNot(fx.ServerUp, NoServer);
        var admin = await NewPageAsync();
        var downloaded = new List<string>();
        admin.Request += (_, r) => downloaded.Add(r.Url);

        await LoginAsAdminAsync(admin);
        Assert.DoesNotContain(downloaded, u => u.Contains("FlowerShop.Admin")); // not downloaded yet

        await admin.GotoAsync(BrowserFixture.BaseUrl + "/admin"); // first visit to /admin triggers the lazy download
        await admin.GetByRole(AriaRole.Heading, new() { Name = "Orders dashboard" }).WaitForAsync();
        Assert.Contains(downloaded, u => u.Contains("FlowerShop.Admin"));
        await Task.Delay(1500); // let the SignalR connection finish starting

        var customer = await NewPageAsync();
        await RegisterAsync(customer, NewEmail());
        await PlaceOrderAsync(customer);

        await admin.GetByText("New order #").WaitForAsync(); // toast pushed by SignalR
    }

    [SkippableFact]
    public async Task Admin_can_add_edit_and_delete_a_product()
    {
        Skip.IfNot(fx.ServerUp, NoServer);
        var admin = await NewPageAsync();
        await LoginAsAdminAsync(admin);
        await admin.GotoAsync(BrowserFixture.BaseUrl + "/admin/products");
        await admin.GetByRole(AriaRole.Heading, new() { Name = "Products" }).WaitForAsync();

        var name = $"E2E Flower {Guid.NewGuid():N}"[..20];
        await admin.GetByLabel("Name").FillAsync(name);
        await admin.GetByLabel("Category").FillAsync("Bouquets");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
        await admin.GetByText(name).WaitForAsync();

        await admin.Locator(".card.row", new() { HasText = name }).GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await admin.GetByText("Edit product #").WaitForAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        await admin.Locator(".card.row", new() { HasText = name }).GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Confirm delete" }).ClickAsync();
        await admin.WaitForFunctionAsync("n => !document.body.innerText.includes(n)", name);
    }

    // ---------- screenshots for the presentation (saved to docs/screenshots) ----------
    static string ShotDir()
    {
        var env = Environment.GetEnvironmentVariable("SCREENSHOT_DIR");
        var dir = env;
        if (dir is null)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "FlowerShop.sln"))) d = d.Parent;
            dir = Path.Combine(d?.FullName ?? Directory.GetCurrentDirectory(), "docs", "screenshots");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    static Task Shot(IPage page, string name) => page.ScreenshotAsync(new() { Path = Path.Combine(ShotDir(), name), FullPage = true });

    [SkippableFact]
    public async Task Capture_screenshots_for_presentation()
    {
        Skip.IfNot(fx.ServerUp, NoServer);
        var b = await fx.BrowserAsync("chromium");
        async Task<IPage> Desktop()
        {
            var p = await (await b.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 800 } })).NewPageAsync();
            p.SetDefaultTimeout(30_000); return p;
        }

        var c = await Desktop();
        await c.GotoAsync(BrowserFixture.BaseUrl + "/");
        await c.Locator(".product").First.WaitForAsync();
        await Shot(c, "01-catalog.png");

        await OpenLoginAsync(c);
        await Shot(c, "02-login.png");
        await RegisterAsync(c, NewEmail());

        await c.Locator(".product").First.GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
        await c.Locator(".product").Nth(1).GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
        await c.Locator("a.cart").ClickAsync();
        await c.GetByText("Subtotal").WaitForAsync();
        await Shot(c, "03-cart.png");

        await c.GetByRole(AriaRole.Link, new() { Name = "Checkout" }).ClickAsync();
        await c.GetByLabel("Delivery address").FillAsync("Kabanbay Batyr 11, apt 7");
        await Shot(c, "04-checkout.png");
        await c.GetByRole(AriaRole.Button, new() { Name = "Place order" }).ClickAsync();
        await c.Locator(".order").First.WaitForAsync();
        await Shot(c, "05-my-orders.png");

        var a = await Desktop();
        await LoginAsAdminAsync(a);
        await a.GotoAsync(BrowserFixture.BaseUrl + "/admin");
        await a.GetByRole(AriaRole.Heading, new() { Name = "Orders dashboard" }).WaitForAsync();
        await a.WaitForTimeoutAsync(800);
        await Shot(a, "06-admin-orders.png");
        await a.GotoAsync(BrowserFixture.BaseUrl + "/admin/products");
        await a.GetByRole(AriaRole.Heading, new() { Name = "Products" }).WaitForAsync();
        await a.Locator(".card.row").First.WaitForAsync();
        await Shot(a, "07-admin-products.png");

        await a.GotoAsync(BrowserFixture.BaseUrl + "/swagger");
        await a.GetByText("FlowerShop API").First.WaitForAsync();
        await Shot(a, "08-swagger.png");

        var phone = await NewPageAsync("webkit", "iPhone 13");
        await phone.GotoAsync(BrowserFixture.BaseUrl + "/");
        await phone.Locator(".product").First.WaitForAsync();
        await Shot(phone, "09-catalog-mobile.png");

        Assert.True(Directory.GetFiles(ShotDir(), "0*.png").Length >= 9);
    }
}
