using FlightTracker.Consumer;
using FlightTracker.Producer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlightTracker.Tests;

public class KafkaConfigurationTests
{
    [Theory]
    [InlineData("Kafka:BootstrapServers")]
    [InlineData("Kafka:Topic")]
    [InlineData("Kafka:GroupId")]
    public void Consumer_Should_Reject_Missing_Kafka_Setting(
        string missingKey)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = "kafka:9092",
            ["Kafka:Topic"] = "flight-observations",
            ["Kafka:GroupId"] = "flighttracker-tests"
        };

        settings.Remove(missingKey);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new Worker(
                NullLogger<Worker>.Instance,
                null!,
                configuration));
    }

    [Theory]
    [InlineData("Kafka:BootstrapServers")]
    [InlineData("Kafka:Topic")]
    public void Producer_Should_Reject_Missing_Kafka_Setting(
        string missingKey)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = "kafka:9092",
            ["Kafka:Topic"] = "flight-observations"
        };

        settings.Remove(missingKey);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
        {
            using var producer = new KafkaProducer(configuration);
        });
    }
}
