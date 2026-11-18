namespace FlowerShop.Shared;

/// <summary>Pure pricing rules (shared by client preview and server source of truth).</summary>
public static class Pricing
{
    public const decimal FreeDeliveryFrom = 15_000m; // KZT
    public const decimal DeliveryFee = 1_500m;

    public static decimal Subtotal(IEnumerable<(decimal Price, int Qty)> lines) => lines.Sum(l => l.Price * l.Qty);
    public static decimal Delivery(decimal subtotal) => subtotal <= 0 || subtotal >= FreeDeliveryFrom ? 0 : DeliveryFee;
    public static decimal Total(decimal subtotal) => subtotal + Delivery(subtotal);
}

public static class OrderValidator
{
    public static Result<CreateOrderRequest> Validate(CreateOrderRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Address) || r.Address.Trim().Length < 8)
            return Result<CreateOrderRequest>.Fail("Delivery address must be at least 8 characters.");
        if (r.Note is { Length: > 200 })
            return Result<CreateOrderRequest>.Fail("Note is too long (max 200).");
        if (r.Items is null || r.Items.Count == 0)
            return Result<CreateOrderRequest>.Fail("Cart is empty.");
        if (r.Items.Any(i => i.Quantity is < 1 or > 50))
            return Result<CreateOrderRequest>.Fail("Quantity must be between 1 and 50.");
        if (r.Items.GroupBy(i => i.ProductId).Any(g => g.Count() > 1))
            return Result<CreateOrderRequest>.Fail("Duplicate products in order.");
        return Result<CreateOrderRequest>.Ok(r with { Address = r.Address.Trim() });
    }
}

/// <summary>
/// Pure order-status state machine (functional core). Pending → Preparing → OutForDelivery → Delivered;
/// an open order can be Cancelled. Delivered and Cancelled are final.
/// </summary>
public static class OrderWorkflow
{
    public static IReadOnlyList<OrderStatus> Next(OrderStatus from) => from switch
    {
        OrderStatus.Pending => new[] { OrderStatus.Preparing, OrderStatus.Cancelled },
        OrderStatus.Preparing => new[] { OrderStatus.OutForDelivery, OrderStatus.Cancelled },
        OrderStatus.OutForDelivery => new[] { OrderStatus.Delivered, OrderStatus.Cancelled },
        _ => Array.Empty<OrderStatus>()
    };

    public static bool IsFinal(OrderStatus s) => Next(s).Count == 0;

    /// <summary>Staying in the same status is allowed (no-op); anything else must follow <see cref="Next"/>.</summary>
    public static bool CanMove(OrderStatus from, OrderStatus to) => from == to || Next(from).Contains(to);
}

public static class AuthValidator
{
    public static Result<RegisterRequest> Validate(RegisterRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.FullName) || r.FullName.Trim().Length < 2) return Result<RegisterRequest>.Fail("Enter your full name.");
        if (string.IsNullOrWhiteSpace(r.Email) || !r.Email.Contains('@') || r.Email.Length > 120) return Result<RegisterRequest>.Fail("Enter a valid email.");
        if (string.IsNullOrEmpty(r.Password) || r.Password.Length < 8 || !r.Password.Any(char.IsDigit) || !r.Password.Any(char.IsLetter))
            return Result<RegisterRequest>.Fail("Password: 8+ characters with letters and digits.");
        return Result<RegisterRequest>.Ok(r with { Email = r.Email.Trim().ToLowerInvariant(), FullName = r.FullName.Trim() });
    }
}

/// <summary>Immutable cart transformations (functional core of the cart).</summary>
public static class CartOps
{
    public static Result<IReadOnlyList<CartLine>> Add(IReadOnlyList<CartLine> cart, ProductDto p, int qty = 1)
    {
        if (p.Stock <= 0) return Result<IReadOnlyList<CartLine>>.Fail($"'{p.Name}' is out of stock.");
        var current = cart.FirstOrDefault(l => l.Product.Id == p.Id)?.Quantity ?? 0;
        var next = Math.Min(current + qty, Math.Min(p.Stock, 50));
        return SetQty(cart, p, next);
    }

    public static Result<IReadOnlyList<CartLine>> SetQty(IReadOnlyList<CartLine> cart, ProductDto p, int qty)
    {
        if (qty <= 0) return Result<IReadOnlyList<CartLine>>.Ok(cart.Where(l => l.Product.Id != p.Id).ToList());
        var capped = Math.Min(qty, Math.Min(p.Stock, 50));
        var exists = cart.Any(l => l.Product.Id == p.Id);
        IReadOnlyList<CartLine> result = exists
            ? cart.Select(l => l.Product.Id == p.Id ? l with { Quantity = capped } : l).ToList()
            : cart.Append(new CartLine(p, capped)).ToList();
        return Result<IReadOnlyList<CartLine>>.Ok(result);
    }
}
