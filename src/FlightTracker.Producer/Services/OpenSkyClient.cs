using System.Text.Json;
using FlightTracker.Producer.Models;

namespace FlightTracker.Producer.Services;

public sealed class OpenSkyClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenSkyClient> _logger;

    public OpenSkyClient(
        IHttpClientFactory httpClientFactory,
        ILogger<OpenSkyClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<FlightObservation>> GetFlightsAsync(
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient("OpenSky");

        const string requestPath =
            "states/all?lamin=40.5&lomin=27.5&lamax=41.7&lomax=30.5";

        _logger.LogInformation("OpenSky isteği gönderiliyor...");

        using var response = await client.GetAsync(
            requestPath,
            cancellationToken);

        _logger.LogInformation(
            "HTTP durum kodu: {StatusCode}",
            (int)response.StatusCode);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var responseTime = root.GetProperty("time").GetInt64();

        _logger.LogInformation(
            "OpenSky yanıt zamanı (UTC): {ResponseTime}",
            DateTimeOffset.FromUnixTimeSeconds(responseTime));

        var flights = new List<FlightObservation>();

        if (!root.TryGetProperty("states", out var states) ||
            states.ValueKind == JsonValueKind.Null)
        {
            _logger.LogInformation(
                "Yanıtta uçak gözlemi bulunmuyor.");

            return flights;
        }

        _logger.LogInformation(
            "Gelen gözlem sayısı: {Count}",
            states.GetArrayLength());

        foreach (var state in states.EnumerateArray())
        {
            var icao24 = state[0].GetString();
            var callsign = state[1].GetString()?.Trim();

            if (string.IsNullOrWhiteSpace(icao24) ||
                state[5].ValueKind == JsonValueKind.Null ||
                state[6].ValueKind == JsonValueKind.Null)
            {
                continue;
            }

          var flight = new FlightObservation
{
    Icao24 = icao24,

    Callsign = string.IsNullOrWhiteSpace(callsign)
        ? null
        : callsign,

    Latitude = state[6].GetDouble(),
    Longitude = state[5].GetDouble(),

    PositionUpdatedAtUtc =
        state[3].ValueKind == JsonValueKind.Null
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(
                state[3].GetInt64()),

    BarometricAltitudeMeters =
        state[7].ValueKind == JsonValueKind.Null
            ? null
            : state[7].GetDouble(),

    GroundSpeedMetersPerSecond =
        state[9].ValueKind == JsonValueKind.Null
            ? null
            : state[9].GetDouble(),

    TrackDegrees =
        state[10].ValueKind == JsonValueKind.Null
            ? null
            : state[10].GetDouble(),

    OnGround =
        state[8].ValueKind == JsonValueKind.Null
            ? null
            : state[8].GetBoolean()
};

            flights.Add(flight);
        }

        return flights;
    }
}