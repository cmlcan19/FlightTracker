using System.Text.Json;
using Confluent.Kafka;
using FlightTracker.Api.Hubs;
using FlightTracker.Api.Models;
using Microsoft.AspNetCore.SignalR;

namespace FlightTracker.Api.Services;

public sealed class LiveFlightConsumer : BackgroundService
{
    private readonly ILogger<LiveFlightConsumer> _logger;
    private readonly IHubContext<FlightsHub> _hub;
    private readonly ConsumerConfig _config;
    private readonly string _topic;

    public LiveFlightConsumer(
        ILogger<LiveFlightConsumer> logger,
        IHubContext<FlightsHub> hub,
        IConfiguration configuration)
    {
        _logger = logger;
        _hub = hub;

        var bootstrapServers = configuration["Kafka:BootstrapServers"];
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
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        };
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var consumer =
            new ConsumerBuilder<string, string>(_config).Build();

        try
        {
            consumer.Subscribe(_topic);

            _logger.LogInformation(
                "Canlı Consumer başladı. Topic: {Topic} | Grup: {GroupId}",
                _topic,
                _config.GroupId);

            while (!stoppingToken.IsCancellationRequested)
            {
                var result = consumer.Consume(stoppingToken);

                FlightObservation? flight;

                try
                {
                    flight = JsonSerializer.Deserialize<FlightObservation>(
                        result.Message?.Value ?? string.Empty);

                    if (flight is null ||
                        string.IsNullOrWhiteSpace(flight.Icao24))
                    {
                        throw new JsonException(
                            "Gecersiz veya eksik ucak kimligi.");
                    }
                }
                catch (JsonException ex)
                {
                    consumer.Commit(result);

                    _logger.LogWarning(
                        "Gecersiz Kafka mesaji atlandi | Topic: {Topic} | Partition: {Partition} | Offset: {Offset} | Sebep: {Reason}",
                        result.Topic,
                        result.Partition.Value,
                        result.Offset.Value,
                        ex.Message);

                    continue;
                }

                await _hub.Clients.All.SendAsync(
    "FlightUpdated",
    new
    {
        flight.Icao24,
        flight.Callsign,
        flight.Latitude,
        flight.Longitude,
        flight.PositionUpdatedAtUtc,
        flight.BarometricAltitudeMeters,
        flight.GroundSpeedMetersPerSecond,
        flight.TrackDegrees,
        flight.OnGround
    },
    stoppingToken);

                consumer.Commit(result);

                _logger.LogInformation(
                    "SignalR yayını | Uçak: {Icao24} | Partition: {Partition} | Offset: {Offset}",
                    flight.Icao24,
                    result.Partition.Value,
                    result.Offset.Value);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Canlı Consumer durduruldu.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Canlı aktarım başarısız. Servis durduruluyor.");

            throw;
        }
        finally
        {
            consumer.Close();
        }
    }
}