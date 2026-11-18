using System.Net;
using Xunit;

namespace FlowerShop.Tests;

public class SwaggerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact] public async Task Openapi_document_is_served_and_lists_endpoints()
    {
        var res = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("api/orders", json); Assert.Contains("api/products", json);
    }
}
