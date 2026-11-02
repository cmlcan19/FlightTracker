
using System.Net;
using System.Text.Json;
using Confluent.Kafka;
using FlightTracker.Producer.Services;

namespace FlightTracker.Producer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly OpenSkyClient _openSkyClient;
    private readonly KafkaProducer _kafkaProducer;
    private readonly TimeSpan _pollInterval;

    public Worker(
        ILogger<Worker> logger,
        OpenSkyClient openSkyClient,
        KafkaProducer kafkaProducer,
        IConfiguration configuration)
    {
        _logger = logger;
        _openSkyClient = openSkyClient;
        _kafkaProducer = kafkaProducer;

        var intervalSeconds = configuration.GetValue<int>(
            "OpenSky:PollIntervalSeconds");

        if (intervalSeconds <= 0)
        {
            throw new InvalidOperationException(
                "OpenSky:PollIntervalSeconds sıfırdan büyük olmalıdır.");
        }

        _pollInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var rateLimitDelay = TimeSpan.FromMinutes(15);

        _logger.LogInformation(
            "Worker başladı. Sorgular arasında {Seconds} saniye beklenecek.",
            _pollInterval.TotalSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var flights = await _openSkyClient.GetFlightsAsync(
                        stoppingToken);

                    // Başarılı HTTP yanıtı: 429 bekleme süresini sıfırla.
                    rateLimitDelay = TimeSpan.FromMinutes(15);

                    _logger.LogInformation(
                        "Modele aktarılan konumlu gözlem sayısı: {Count}",
                        flights.Count);

                    foreach (var flight in flights.Take(5))
                    {
                        _logger.LogInformation(
                            "Uçak: {Icao24} | Çağrı kodu: {Callsign} | Enlem: {Latitude} | Boylam: {Longitude} | Konum zamanı (UTC): {PositionTime}",
                            flight.Icao24,
                            flight.Callsign ?? "Bilinmiyor",
                            flight.Latitude,
                            flight.Longitude,
                            flight.PositionUpdatedAtUtc?.ToString("O")
                                ?? "Bilinmiyor");
                    }

                    var publishedCount = 0;

                    foreach (var flight in flights)
                    {
                        await _kafkaProducer.PublishAsync(
                            flight,
                            stoppingToken);

                        publishedCount++;
                    }

                    _logger.LogInformation(
                        "Kafka'ya teslim edilen gözlem sayısı: {Count}",
                        publishedCount);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "İşlem zaman aşımına uğradı veya iptal edildi.");
                }
                catch (HttpRequestException ex)
                    when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning(
                        "OpenSky HTTP 429. {Minutes} dakika beklenecek.",
                        rateLimitDelay.TotalMinutes);

                    await Task.Delay(
                        rateLimitDelay,
                        stoppingToken);

                    rateLimitDelay = TimeSpan.FromMinutes(
                        Math.Min(
                            rateLimitDelay.TotalMinutes * 2,
                            60));

                    continue;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(
                        ex,
                        "OpenSky isteğinde hata oluştu.");
                }
                catch (JsonException ex)
                {
                    _logger.LogError(
                        ex,
                        "OpenSky yanıtı geçerli JSON olarak okunamadı.");
                }
                catch (KafkaException ex)
                {
                    _logger.LogError(
                        ex,
                        "Kafka gönderiminde hata oluştu. Bu turun gönderimi kesildi.");
                }

                _logger.LogInformation(
                    "Sonraki sorgudan önce {Seconds} saniye bekleniyor...",
                    _pollInterval.TotalSeconds);

                await Task.Delay(
                    _pollInterval,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker durduruldu.");
        }
    }
}
