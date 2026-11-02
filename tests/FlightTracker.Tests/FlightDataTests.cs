using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FlightTracker.Tests;

public class FlightDataTests
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://localhost:5050/"),
        Timeout = TimeSpan.FromSeconds(20)
    };

    [Fact]
    public async Task LatestFlights_Should_Have_Valid_Coordinates()
    {
        var flights = await Client.GetFromJsonAsync<JsonElement>(
            "api/flights/latest");

        Assert.Equal(JsonValueKind.Array, flights.ValueKind);

        foreach (var flight in flights.EnumerateArray())
        {
            var icao24 = flight.GetProperty("icao24").GetString();
            var latitude = flight.GetProperty("latitude").GetDouble();
            var longitude = flight.GetProperty("longitude").GetDouble();

            Assert.False(string.IsNullOrWhiteSpace(icao24));
            Assert.InRange(latitude, -90, 90);
            Assert.InRange(longitude, -180, 180);
            Assert.True(double.IsFinite(latitude));
            Assert.True(double.IsFinite(longitude));
        }
    }

    [Fact]
    public async Task LatestFlights_Should_Have_Unique_Aircraft()
    {
        var flights = await Client.GetFromJsonAsync<JsonElement>(
            "api/flights/latest");

        Assert.Equal(JsonValueKind.Array, flights.ValueKind);

        var aircraftIds = flights.EnumerateArray()
            .Select(flight =>
                flight.GetProperty("icao24").GetString())
            .ToList();

        Assert.Equal(
            aircraftIds.Count,
            aircraftIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task RecentFlights_Should_Return_Valid_Array()
    {
        var flights = await Client.GetFromJsonAsync<JsonElement>(
            "api/flights/latest?onlyRecent=true");

        Assert.Equal(JsonValueKind.Array, flights.ValueKind);

        foreach (var flight in flights.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(
                flight.GetProperty("icao24").GetString()));
        }
    }
}
