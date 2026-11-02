using System.Text.Json;
using FlightTracker.Consumer.Services;
using Xunit;

namespace FlightTracker.Tests;

public class FlightMessageParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{not json")]
    [InlineData("{\"Icao24\":\"\"}")]
    [InlineData("{\"Icao24\":\"   \"}")]
    public void Parse_Should_Reject_Invalid_Messages(string payload)
    {
        Assert.Throws<JsonException>(() =>
        {
            FlightMessageParser.Parse(payload);
        });
    }

    [Fact]
    public void Parse_Should_Accept_Valid_Flight()
    {
        const string payload = """
            {
                "Icao24": "4bc8ce",
                "Latitude": 41.0,
                "Longitude": 29.0
            }
            """;

        var flight = FlightMessageParser.Parse(payload);

        Assert.Equal("4bc8ce", flight.Icao24);
        Assert.Equal(41.0, flight.Latitude);
        Assert.Equal(29.0, flight.Longitude);
    }
}
