using Bunit;
using FlowerShop.Client.Components;
using FlowerShop.Shared;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace FlowerShop.Tests;

public class ComponentTests : TestContext
{
    static ProductDto Rose(int stock = 5) => new(1, "Red Roses", "Classic", 18900, "Bouquets", "🌹", stock);

    [Fact] public void ProductCard_renders_and_raises_callback()
    {
        ProductDto? added = null;
        var cut = RenderComponent<ProductCard>(p => p
            .Add(x => x.Product, Rose())
            .Add(x => x.OnAdd, EventCallback.Factory.Create<ProductDto>(this, d => added = d)));
        Assert.Contains("Red Roses", cut.Markup);
        cut.Find("button").Click();
        Assert.Equal(1, added?.Id);
    }

    [Fact] public void ProductCard_sold_out_has_no_add_button()
    {
        var cut = RenderComponent<ProductCard>(p => p.Add(x => x.Product, Rose(0)));
        Assert.Empty(cut.FindAll("button")); Assert.Contains("Sold out", cut.Markup);
    }

    [Fact] public void StatusBadge_uses_readable_label()
    {
        var cut = RenderComponent<StatusBadge>(p => p.Add(x => x.Status, OrderStatus.OutForDelivery));
        Assert.Contains("Out for delivery", cut.Markup);
    }

    [Fact] public void DataList_shows_empty_template_and_items()
    {
        var empty = RenderComponent<DataList<int>>(p => p.Add(x => x.Items, new List<int>()).Add(x => x.ItemTemplate, i => $"<i>{i}</i>"));
        Assert.Contains("Nothing here yet", empty.Markup);
        var full = RenderComponent<DataList<int>>(p => p.Add(x => x.Items, new List<int> { 1, 2 }).Add(x => x.ItemTemplate, i => $"<i>{i}</i>"));
        Assert.Equal(2, full.FindAll("i").Count);
    }
}
