
using System.Text.Json;
using Confluent.Kafka;
using FlightTracker.Consumer.Models;
using FlightTracker.Consumer.Services;
using Npgsql;

namespace FlightTracker.Consumer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly FlightObservationRepository _repository;
    private readonly ConsumerConfig _config;
    private readonly string _topic;

    public Worker(
        ILogger<Worker> logger,
        FlightObservationRepository repository,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;

        var bootstrapServers =
            configuration["Kafka:BootstrapServers"];

        var topic = configuration["Kafka:Topic"];
        var groupId = configuration["Kafka:GroupId"];

        if (string.IsNullOrWhiteSpace(bootstrapServers) ||
            string.IsNullOrWhiteSpace(topic) ||
            string.IsNullOrWhiteSpace(groupId))
        {
            throw new InvalidOperationException(
                "Kafka bağlantı, topic ve grup ayarları bulunmalıdır.");
        }

        _topic = topic;

        _config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        };
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var consumer =
            new ConsumerBuilder<string, string>(_config).Build();

        using var dlqPublisher = new DeadLetterPublisher(
            _config.BootstrapServers!,
            $"{_topic}-dlq");

        try
        {
            consumer.Subscribe(_topic);

            _logger.LogInformation(
                "Consumer başladı. Topic: {Topic} | Grup: {GroupId}",
                _topic,
                _config.GroupId);

            while (!stoppingToken.IsCancellationRequested)
            {
                var result = consumer.Consume(stoppingToken);

                FlightObservation flight;

                try
                {
                    flight = FlightMessageParser.Parse(
                        result.Message?.Value);
                }
                catch (JsonException ex)
                {
                    await dlqPublisher.PublishAsync(
                        result,
                        ex,
                        stoppingToken);

                    consumer.Commit(result);

                    _logger.LogWarning(
                        "Mesaj DLQ'ya gonderildi | Topic: {Topic} | Partition: {Partition} | Offset: {Offset}",
                        result.Topic,
                        result.Partition.Value,
                        result.Offset.Value);

                    continue;
                }
                // Veritabanına başarıyla yazılmadan
                // Kafka offset'i kaydedilmez.
                var inserted = await SaveWithRetryAsync(
                    flight,
                    result,
                    stoppingToken);

                consumer.Commit(result);

                _logger.LogInformation(
                    "PostgreSQL: {Result} | Uçak: {Icao24} | Partition: {Partition} | Offset: {Offset}",
                    inserted ? "Kaydedildi" : "Zaten kayıtlı",
                    flight.Icao24,
                    result.Partition.Value,
                    result.Offset.Value);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Consumer durduruldu.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "Geçersiz Kafka mesajı. Offset atlanmadı.");

            throw;
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(
                ex,
                "PostgreSQL hatası. Consumer yeniden başlatılacak.");

            throw;
        }
        catch (KafkaException ex)
        {
            _logger.LogError(
                ex,
                "Kafka okuma veya offset kaydetme hatası.");

            throw;
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task<bool> SaveWithRetryAsync(
        FlightObservation flight,
        ConsumeResult<string, string> result,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            try
            {
                return await _repository.SaveAsync(
                    flight,
                    result.Topic,
                    result.Partition.Value,
                    result.Offset.Value,
                    cancellationToken);
            }
            catch (NpgsqlException ex)
                when (ex.IsTransient
                    && attempt < 6
                    && !cancellationToken.IsCancellationRequested)
            {
                attempt++;

                var seconds = Math.Min(
                    30,
                    1 << attempt);

                _logger.LogWarning(
                    ex,
                    "Geçici PostgreSQL hatası. Deneme: {Attempt}/6. {Seconds} saniye beklenecek.",
                    attempt,
                    seconds);

                await Task.Delay(
                    TimeSpan.FromSeconds(seconds),
                    cancellationToken);
            }
        }
    }
}
