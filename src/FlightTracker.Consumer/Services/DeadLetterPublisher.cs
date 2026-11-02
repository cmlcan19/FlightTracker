using System.Text.Json;
using Confluent.Kafka;

namespace FlightTracker.Consumer.Services;

public sealed class DeadLetterPublisher : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;

    public DeadLetterPublisher(string bootstrapServers, string topic)
    {
        if (string.IsNullOrWhiteSpace(bootstrapServers) ||
            string.IsNullOrWhiteSpace(topic))
        {
            throw new ArgumentException("Kafka DLQ ayarlari eksik.");
        }

        _topic = topic;

        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                ClientId = "flighttracker-dlq",
                EnableIdempotence = true,
                Acks = Acks.All,
                MessageTimeoutMs = 15000
            }).Build();
    }

    public static Message<string, string> BuildMessage(
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        string? originalKey,
        string? originalValue,
        string reason)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sourceTopic,
            sourcePartition,
            sourceOffset,
            originalKey,
            originalValue,
            reason,
            failedAtUtc = DateTimeOffset.UtcNow
        });

        return new Message<string, string>
        {
            Key = $"{sourceTopic}:{sourcePartition}:{sourceOffset}",
            Value = payload
        };
    }

    public async Task PublishAsync(
        ConsumeResult<string, string> result,
        JsonException exception,
        CancellationToken cancellationToken)
    {
        var message = BuildMessage(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            result.Message?.Key,
            result.Message?.Value,
            exception.Message);

        await _producer.ProduceAsync(
            _topic,
            message,
            cancellationToken);
    }

    public void Dispose()
    {
        _producer.Dispose();
    }
}
