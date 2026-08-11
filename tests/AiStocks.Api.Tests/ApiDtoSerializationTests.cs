using System.Text.Json;
using AiStocks.Api;

namespace AiStocks.Api.Tests;

public sealed class ApiDtoSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void IdentityDto_uses_stable_json_property_names()
    {
        var json = JsonSerializer.Serialize(new IdentityDto("viewer@example.com", "viewer"), Options);

        Assert.Equal("{\"email\":\"viewer@example.com\",\"role\":\"viewer\"}", json);
    }


    [Fact]
    public void ProblemDto_uses_stable_error_shape()
    {
        var json = JsonSerializer.Serialize(new ApiProblemDto("market-data-unavailable", "Market data unavailable", 503, "trace-1"), Options);

        Assert.Equal("{\"code\":\"market-data-unavailable\",\"title\":\"Market data unavailable\",\"status\":503,\"traceId\":\"trace-1\",\"detail\":null}", json);
    }
}
