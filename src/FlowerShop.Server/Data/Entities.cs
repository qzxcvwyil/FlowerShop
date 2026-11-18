using FlowerShop.Shared;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.Server.Data;

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Customer";
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string Category { get; set; } = "";
    public string Emoji { get; set; } = "💐";
    public int Stock { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public OrderStatus Status { get; set; }
    public string Address { get; set; } = "";
    public string? Note { get; set; }
    public decimal Delivery { get; set; }
    public decimal Total { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<Product>().Property(p => p.Stock).IsConcurrencyToken(); // optimistic concurrency on stock
        b.Entity<Product>().HasIndex(p => p.Category);
        b.Entity<Order>().HasIndex(o => o.UserId);
        b.Entity<Order>().Property(o => o.Status).HasConversion<string>();
    }
}

public static class Seeder
{
    public static void Seed(ShopDb db, IConfiguration cfg)
    {
        if (!db.Users.Any())
            db.Users.Add(new User
            {
                Email = cfg["Seed:AdminEmail"]!, FullName = "Shop Admin", Role = "Admin",
                PasswordHash = PasswordHasher.Hash(cfg["Seed:AdminPassword"]!)
            });
        if (!db.Products.Any())
            db.Products.AddRange(
                P("Red Roses (15)", "Classic long-stem roses, wrapped in kraft paper.", 18900, "Bouquets", "🌹", 20),
                P("Spring Tulips (25)", "Fresh Dutch tulips in mixed pastel colours.", 14500, "Bouquets", "🌷", 30),
                P("Sunflower Joy", "Bright sunflowers with greenery.", 9800, "Bouquets", "🌻", 25),
                P("Peony Cloud", "Seasonal peonies, soft pink.", 24900, "Bouquets", "🌸", 12),
                P("Lavender Bundle", "Dried lavender, lasts for months.", 5200, "Bouquets", "🪻", 40),
                P("Wedding Arch Set", "Full arrangement for ceremony, delivery included.", 129000, "Events", "💒", 3),
                P("Table Centrepiece", "Low arrangement for dinners and banquets.", 21500, "Events", "💐", 15),
                P("Orchid Phalaenopsis", "White orchid in ceramic pot.", 16500, "Plants", "🪴", 18),
                P("Monstera Deliciosa", "Large-leaf houseplant, 40 cm.", 12900, "Plants", "🌿", 22),
                P("Succulent Trio", "Three mini succulents in clay pots.", 6900, "Plants", "🌵", 35),
                P("Ceramic Vase", "Hand-made vase, matte white.", 7500, "Extras", "🏺", 28),
                P("Greeting Card", "Handwritten card with your message.", 900, "Extras", "💌", 200));
        db.SaveChanges();

        static Product P(string n, string d, decimal price, string c, string e, int s) =>
            new() { Name = n, Description = d, Price = price, Category = c, Emoji = e, Stock = s };
    }
}
