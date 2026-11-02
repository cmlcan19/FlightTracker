using System.Text.Json;
using Confluent.Kafka;
using FlightTracker.Producer.Models;

namespace FlightTracker.Producer.Services;

public sealed class KafkaProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;

    public KafkaProducer(IConfiguration configuration)
    {
        var bootstrapServers = configuration["Kafka:BootstrapServers"];

        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            throw new InvalidOperationException(
                "Kafka:BootstrapServers ayarı bulunamadı.");
        }

        var topic = configuration["Kafka:Topic"];

        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new InvalidOperationException(
                "Kafka:Topic ayarı bulunamadı.");
        }

        _topic = topic;

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = "flighttracker-producer",
            EnableIdempotence = true,
            MessageTimeoutMs = 15000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(
        FlightObservation flight,
        CancellationToken cancellationToken)
    {
        var message = new Message<string, string>
        {
            Key = flight.Icao24,
            Value = JsonSerializer.Serialize(flight)
        };

        await _producer.ProduceAsync(
            _topic,
            message,
            cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}