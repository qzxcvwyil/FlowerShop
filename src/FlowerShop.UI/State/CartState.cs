using FlowerShop.Shared;

namespace FlowerShop.Client.State;

/// <summary>
/// State container (scoped service + CascadingValue). Holds an IMMUTABLE list; all changes go through
/// the pure functions in CartOps, then the container swaps the reference and notifies subscribers.
/// </summary>
public sealed class CartState
{
    public IReadOnlyList<CartLine> Lines { get; private set; } = [];
    public event Action? Changed;

    public int Count => Lines.Sum(l => l.Quantity);
    public decimal Subtotal => Pricing.Subtotal(Lines.Select(l => (l.Product.Price, l.Quantity)));
    public decimal Delivery => Pricing.Delivery(Subtotal);
    public decimal Total => Pricing.Total(Subtotal);

    public Result<int> Add(ProductDto p, int qty = 1) => Apply(CartOps.Add(Lines, p, qty));
    public Result<int> SetQuantity(ProductDto p, int qty) => Apply(CartOps.SetQty(Lines, p, qty));
    public void Remove(ProductDto p) => Apply(CartOps.SetQty(Lines, p, 0));
    public void Clear() { Lines = []; Changed?.Invoke(); }
    public void Restore(IReadOnlyList<CartLine> lines) { Lines = lines; Changed?.Invoke(); }

    Result<int> Apply(Result<IReadOnlyList<CartLine>> r)
    {
        if (!r.IsOk) return Result<int>.Fail(r.Error!);
        Lines = r.Value!;
        Changed?.Invoke();
        return Result<int>.Ok(Count);
    }
}
