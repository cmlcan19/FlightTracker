using System.Text.Json;
using FlightTracker.Consumer.Models;

namespace FlightTracker.Consumer.Services;

public static class FlightMessageParser
{
    public static FlightObservation Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("Kafka mesajı boş.");
        }

        var flight = JsonSerializer.Deserialize<FlightObservation>(json)
            ?? throw new JsonException(
                "Mesajdan uçak gözlemi oluşturulamadı.");

        if (string.IsNullOrWhiteSpace(flight.Icao24))
        {
            throw new JsonException(
                "Mesajdaki uçak kimliği boş.");
        }

        return flight;
    }
}
