using System.Net;
using System.Text.Json;
using Xunit;

namespace FlightTracker.Tests;

public class ApiIntegrationTests
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://localhost:5050/"),
        Timeout = TimeSpan.FromSeconds(20)
    };

    [Theory]
    [InlineData("api/flights/latest")]
    [InlineData("api/flights/latest?onlyRecent=true")]
    [InlineData("api/flights/4bc8ce/sessions?limit=500")]
    [InlineData("api/flights/archive?fromUtc=2026-10-13T21%3A00%3A00Z&toUtc=2026-10-14T21%3A00%3A00Z&limit=1000")]
    public async Task Api_Should_Return_Valid_Json_Array(string endpoint)
    {
        using var response = await Client.GetAsync(endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(content);

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }
}
