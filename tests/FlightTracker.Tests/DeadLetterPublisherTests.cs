using System.Text.Json;
using FlightTracker.Consumer.Services;
using Xunit;

namespace FlightTracker.Tests;

public class DeadLetterPublisherTests
{
    [Fact]
    public void BuildMessage_Should_Preserve_Original_Message()
    {
        var message = DeadLetterPublisher.BuildMessage(
            "flight-observations",
            0,
            123,
            "test-key",
            "{invalid json",
            "Invalid JSON");

        Assert.Equal("flight-observations:0:123", message.Key);

        using var json = JsonDocument.Parse(message.Value);

        Assert.Equal(
            "{invalid json",
            json.RootElement.GetProperty("originalValue").GetString());

        Assert.Equal(
            123,
            json.RootElement.GetProperty("sourceOffset").GetInt64());

        Assert.Equal(
            "Invalid JSON",
            json.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void BuildMessage_Should_Preserve_Null_Payload()
    {
        var message = DeadLetterPublisher.BuildMessage(
            "flight-observations",
            0,
            124,
            null,
            null,
            "Empty message");

        using var json = JsonDocument.Parse(message.Value);

        Assert.Equal(
            JsonValueKind.Null,
            json.RootElement.GetProperty("originalValue").ValueKind);
    }
}
