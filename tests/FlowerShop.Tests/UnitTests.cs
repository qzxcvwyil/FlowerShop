using System.Text;
using FlowerShop.Client.Services;
using FlowerShop.Client.State;
using FlowerShop.Server;
using FlowerShop.Shared;
using Xunit;

namespace FlowerShop.Tests;

public class PricingTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5000, 1500)]
    [InlineData(14999, 1500)]
    [InlineData(15000, 0)]
    [InlineData(50000, 0)]
    public void Delivery_fee_rules(decimal subtotal, decimal expected) => Assert.Equal(expected, Pricing.Delivery(subtotal));

    [Fact] public void Total_includes_delivery() => Assert.Equal(6500, Pricing.Total(5000));
    [Fact] public void Subtotal_sums_lines() => Assert.Equal(2 * 100 + 3 * 50, Pricing.Subtotal([(100m, 2), (50m, 3)]));
}

public class ValidatorTests
{
    static CreateOrderRequest Req(string addr = "Kabanbay Batyr 1, apt 5", params OrderItemRequest[] items) =>
        new(addr, null, items.Length == 0 ? [new(1, 1)] : items);

    [Fact] public void Valid_order_is_ok_and_trimmed() => Assert.Equal("Kabanbay Batyr 1", OrderValidator.Validate(Req("  Kabanbay Batyr 1  ")).Value!.Address);
    [Fact] public void Short_address_fails() => Assert.False(OrderValidator.Validate(Req("abc")).IsOk);
    [Fact] public void Empty_cart_fails() => Assert.False(OrderValidator.Validate(new("Long enough address", null, [])).IsOk);
    [Theory] [InlineData(0)] [InlineData(51)] [InlineData(-3)]
    public void Bad_quantity_fails(int q) => Assert.False(OrderValidator.Validate(Req(items: new OrderItemRequest(1, q))).IsOk);
    [Fact] public void Duplicate_products_fail() => Assert.False(OrderValidator.Validate(Req(items: [new(1, 1), new(1, 2)])).IsOk);

    [Theory]
    [InlineData("a@b.kz", "password1", true)]
    [InlineData("nope", "password1", false)]
    [InlineData("a@b.kz", "short1", false)]
    [InlineData("a@b.kz", "onlyletters", false)]
    public void Registration_rules(string email, string pw, bool ok) =>
        Assert.Equal(ok, AuthValidator.Validate(new(email, pw, "Madina A")).IsOk);
}

public class CartTests
{
    static ProductDto P(int id, decimal price = 1000, int stock = 5) => new(id, $"P{id}", "", price, "c", "🌹", stock);

    [Fact] public void Adding_same_product_increases_quantity()
    {
        var cart = new CartState(); cart.Add(P(1)); cart.Add(P(1));
        Assert.Equal(2, cart.Count); Assert.Single(cart.Lines);
    }

    [Fact] public void Quantity_is_capped_by_stock()
    {
        var cart = new CartState(); cart.Add(P(1, stock: 3), 10);
        Assert.Equal(3, cart.Count);
    }

    [Fact] public void Out_of_stock_returns_error_not_exception()
    {
        var r = new CartState().Add(P(1, stock: 0));
        Assert.False(r.IsOk); Assert.Contains("out of stock", r.Error);
    }

    [Fact] public void Setting_zero_removes_line()
    {
        var cart = new CartState(); cart.Add(P(1)); cart.SetQuantity(P(1), 0);
        Assert.Empty(cart.Lines);
    }

    [Fact] public void Totals_follow_delivery_rule()
    {
        var cart = new CartState(); cart.Add(P(1, 5000));
        Assert.Equal(6500, cart.Total);
        cart.Add(P(2, 10000));
        Assert.Equal(15000, cart.Total); // free delivery
    }

    [Fact] public void Changed_event_fires_and_original_list_is_untouched()
    {
        var cart = new CartState(); var fired = 0; cart.Changed += () => fired++;
        var before = cart.Lines; cart.Add(P(1));
        Assert.Equal(1, fired); Assert.Empty(before);
    }
}

public class SecurityTests
{
    static string Jwt(string json) => $"h.{Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.s";

    [Fact] public void Password_hash_roundtrip()
    {
        var h = PasswordHasher.Hash("Secret123");
        Assert.True(PasswordHasher.Verify("Secret123", h)); Assert.False(PasswordHasher.Verify("secret123", h));
        Assert.NotEqual(h, PasswordHasher.Hash("Secret123")); // random salt
    }

    [Fact] public void Jwt_claims_are_parsed_into_principal()
    {
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var user = JwtAuthStateProvider.Parse(Jwt($"{{\"name\":\"Ann\",\"role\":\"Admin\",\"exp\":{exp}}}")).User;
        Assert.True(user.Identity!.IsAuthenticated); Assert.True(user.IsInRole("Admin")); Assert.Equal("Ann", user.Identity.Name);
    }

    [Fact] public void Expired_or_garbage_token_is_anonymous()
    {
        Assert.False(JwtAuthStateProvider.Parse(Jwt("{\"name\":\"A\",\"exp\":1}")).User.Identity!.IsAuthenticated);
        Assert.False(JwtAuthStateProvider.Parse("not-a-jwt").User.Identity!.IsAuthenticated);
        Assert.False(JwtAuthStateProvider.Parse(null).User.Identity!.IsAuthenticated);
    }
}
