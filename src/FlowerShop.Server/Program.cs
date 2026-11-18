using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using FlowerShop.Server;
using FlowerShop.Server.Data;
using FlowerShop.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// Safety net: a deployment that declares ASPNETCORE_ENVIRONMENT=Production must not use the placeholder signing key from appsettings.json.
if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production" && (cfg["Jwt:Key"] ?? "").StartsWith("DEV-ONLY", StringComparison.Ordinal))
    throw new InvalidOperationException("Set Jwt__Key (32+ characters) via environment variable or user-secrets for Production.");

builder.Services.AddDbContext<ShopDb>(o => o.UseSqlite(cfg.GetConnectionString("Default") ?? "Data Source=flowershop.db"));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "FlowerShop API", Version = "v1", Description = "Catalog, auth (JWT) and orders. Admin endpoints need the Admin role." });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "Paste the token returned by /api/auth/login" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = new List<string>()
    });
});
builder.Services.AddHealthChecks();
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
builder.Services.AddOutputCache(o => o.AddPolicy("catalog", p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByQuery("*").Tag("products")));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddFixedWindowLimiter("auth", l => { l.PermitLimit = 30; l.Window = TimeSpan.FromMinutes(1); });
});
builder.Services.AddAuthentication("Bearer").AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = TokenService.Validation(cfg);
    o.Events = new()
    {
        OnMessageReceived = c => // SignalR sends JWT via query string
        {
            var t = c.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(t) && c.HttpContext.Request.Path.StartsWithSegments("/hubs")) c.Token = t;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorizationBuilder().AddPolicy("Admin", p => p.RequireRole("Admin"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
    db.Database.EnsureCreated();
    Seeder.Seed(db, cfg);
}

app.Use(async (ctx, next) => // basic security headers for every response
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    ctx.RequestServices.GetRequiredService<ILogger<Program>>().LogError(ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error, "Unhandled error");
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 500, Title = "Server error", Detail = "Something went wrong. Please try again." });
}));
app.UseResponseCompression();
app.UseSwagger();
app.UseSwaggerUI(); // API docs at /swagger
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

static IResult Problem(string detail, int code) => Results.Problem(detail: detail, statusCode: code);
static OrderDto OrderToDto(Order o) => new(o.Id, o.CreatedUtc, o.Status, o.Address, o.User?.FullName ?? "", o.Delivery, o.Total,
    o.Lines.Select(l => new OrderLineDto(l.ProductId, l.Name, l.Quantity, l.UnitPrice)).ToList());
static ProductDto ProductToDto(Product p) => new(p.Id, p.Name, p.Description, p.Price, p.Category, p.Emoji, p.Stock);
static int UserId(ClaimsPrincipal u) => int.Parse(u.FindFirstValue("sub")!);

var api = app.MapGroup("/api");

// ---------- auth ----------
api.MapPost("/auth/register", async (RegisterRequest req, ShopDb db, TokenService tokens) =>
{
    var v = AuthValidator.Validate(req);
    if (!v.IsOk) return Problem(v.Error!, 400);
    var r = v.Value!;
    if (await db.Users.AnyAsync(u => u.Email == r.Email)) return Problem("Email is already registered.", 409);
    var user = new User { Email = r.Email, FullName = r.FullName, PasswordHash = PasswordHasher.Hash(r.Password) };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok(new AuthResponse(tokens.Issue(user), user.Email, user.FullName, user.Role));
}).RequireRateLimiting("auth");

api.MapPost("/auth/login", async (LoginRequest req, ShopDb db, TokenService tokens) =>
{
    var email = req.Email?.Trim().ToLowerInvariant() ?? "";
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (user is null || !PasswordHasher.Verify(req.Password ?? "", user.PasswordHash)) return Problem("Invalid email or password.", 401);
    return Results.Ok(new AuthResponse(tokens.Issue(user), user.Email, user.FullName, user.Role));
}).RequireRateLimiting("auth");

// ---------- catalog ----------
api.MapGet("/products", async (ShopDb db, string? category, string? search, int page = 1, int pageSize = 12) =>
{
    pageSize = Math.Clamp(pageSize, 1, 50); page = Math.Max(page, 1);
    var q = db.Products.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(category)) q = q.Where(p => p.Category == category);
    if (!string.IsNullOrWhiteSpace(search)) q = q.Where(p => EF.Functions.Like(p.Name, $"%{search}%"));
    var total = await q.CountAsync();
    var items = await q.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
    return new PagedResult<ProductDto>(items.Select(ProductToDto).ToList(), total, page, pageSize);
}).CacheOutput("catalog");

api.MapGet("/categories", async (ShopDb db) => await db.Products.AsNoTracking().Select(p => p.Category).Distinct().OrderBy(c => c).ToListAsync())
   .CacheOutput("catalog");

api.MapPost("/products", async (ProductUpsert r, ShopDb db, IOutputCacheStore cache) =>
{
    if (string.IsNullOrWhiteSpace(r.Name) || r.Price <= 0 || r.Stock < 0) return Problem("Name, positive price and non-negative stock are required.", 400);
    var p = new Product { Name = r.Name.Trim(), Description = r.Description, Price = r.Price, Category = r.Category, Emoji = r.Emoji, Stock = r.Stock };
    db.Products.Add(p); await db.SaveChangesAsync(); await cache.EvictByTagAsync("products", default);
    return Results.Created($"/api/products/{p.Id}", ProductToDto(p));
}).RequireAuthorization("Admin");

api.MapPut("/products/{id:int}", async (int id, ProductUpsert r, ShopDb db, IOutputCacheStore cache) =>
{
    var p = await db.Products.FindAsync(id);
    if (p is null) return Problem("Product not found.", 404);
    if (string.IsNullOrWhiteSpace(r.Name) || r.Price <= 0 || r.Stock < 0) return Problem("Invalid product data.", 400);
    (p.Name, p.Description, p.Price, p.Category, p.Emoji, p.Stock) = (r.Name.Trim(), r.Description, r.Price, r.Category, r.Emoji, r.Stock);
    await db.SaveChangesAsync(); await cache.EvictByTagAsync("products", default);
    return Results.Ok(ProductToDto(p));
}).RequireAuthorization("Admin");

api.MapDelete("/products/{id:int}", async (int id, ShopDb db, IOutputCacheStore cache) =>
{
    var p = await db.Products.FindAsync(id);
    if (p is null) return Problem("Product not found.", 404);
    db.Products.Remove(p); await db.SaveChangesAsync(); await cache.EvictByTagAsync("products", default);
    return Results.NoContent();
}).RequireAuthorization("Admin");

// ---------- orders ----------
api.MapPost("/orders", async (CreateOrderRequest req, ClaimsPrincipal me, ShopDb db, IHubContext<OrderHub> hub, IOutputCacheStore cache) =>
{
    var v = OrderValidator.Validate(req);
    if (!v.IsOk) return Problem(v.Error!, 400);
    req = v.Value!;

    var ids = req.Items.Select(i => i.ProductId).ToList();
    var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
    if (products.Count != ids.Count) return Problem("Some products no longer exist.", 400);
    foreach (var i in req.Items)
        if (products[i.ProductId].Stock < i.Quantity) return Problem($"Not enough '{products[i.ProductId].Name}' in stock.", 409);

    // price is ALWAYS computed on the server, never trusted from the client
    var lines = req.Items.Select(i => new OrderLine { ProductId = i.ProductId, Name = products[i.ProductId].Name, Quantity = i.Quantity, UnitPrice = products[i.ProductId].Price }).ToList();
    var subtotal = Pricing.Subtotal(lines.Select(l => (l.UnitPrice, l.Quantity)));
    var order = new Order { UserId = UserId(me), Address = req.Address, Note = req.Note, Status = OrderStatus.Pending, Delivery = Pricing.Delivery(subtotal), Total = Pricing.Total(subtotal), Lines = lines };
    foreach (var i in req.Items) products[i.ProductId].Stock -= i.Quantity;
    db.Orders.Add(order);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateConcurrencyException) { return Problem("Stock changed while ordering. Please review your cart and try again.", 409); }

    await cache.EvictByTagAsync("products", default);
    await db.Entry(order).Reference(o => o.User).LoadAsync();
    var dto = OrderToDto(order);
    await hub.Clients.Group("admins").SendAsync("NewOrder", dto);
    return Results.Created($"/api/orders/{order.Id}", dto);
}).RequireAuthorization();

api.MapGet("/orders/mine", async (ClaimsPrincipal me, ShopDb db) =>
{
    var id = UserId(me);
    var list = await db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.User).Where(o => o.UserId == id).OrderByDescending(o => o.CreatedUtc).ToListAsync();
    return list.Select(OrderToDto).ToList();
}).RequireAuthorization();

api.MapGet("/orders", async (ShopDb db) =>
{
    var list = await db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.User).OrderByDescending(o => o.CreatedUtc).Take(500).ToListAsync();
    return list.Select(OrderToDto).ToList();
}).RequireAuthorization("Admin");

api.MapPut("/orders/{id:int}/status", async (int id, UpdateStatusRequest r, ShopDb db, IHubContext<OrderHub> hub, IOutputCacheStore cache) =>
{
    var o = await db.Orders.Include(x => x.Lines).Include(x => x.User).FirstOrDefaultAsync(x => x.Id == id);
    if (o is null) return Problem("Order not found.", 404);
    if (OrderWorkflow.IsFinal(o.Status)) return Problem("Order is already closed.", 409);
    if (!OrderWorkflow.CanMove(o.Status, r.Status)) return Problem($"Cannot change status from {o.Status} to {r.Status}.", 409); // state machine lives in Shared
    var cancelling = r.Status == OrderStatus.Cancelled && o.Status != OrderStatus.Cancelled;
    if (cancelling) // a cancelled order returns its items to stock
    {
        var ids = o.Lines.Select(l => l.ProductId).ToList();
        var products = await db.Products.Where(p => ids.Contains(p.Id)).ToListAsync();
        foreach (var l in o.Lines) { var p = products.FirstOrDefault(x => x.Id == l.ProductId); if (p is not null) p.Stock += l.Quantity; }
    }
    o.Status = r.Status;
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateConcurrencyException) { return Problem("Stock changed meanwhile. Please try again.", 409); }
    if (cancelling) await cache.EvictByTagAsync("products", default);
    await hub.Clients.Group($"user-{o.UserId}").SendAsync("OrderStatusChanged", o.Id, o.Status);
    return Results.Ok(OrderToDto(o));
}).RequireAuthorization("Admin");

app.MapHub<OrderHub>("/hubs/orders");
app.MapHealthChecks("/health"); // liveness probe for Docker / load balancers
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
