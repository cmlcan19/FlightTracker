
namespace FlightTracker.Api.Models;

public sealed class FlightSession
{
    public required string Icao24 { get; init; }

    public string? Callsign { get; init; }

    public required DateTimeOffset StartTimeUtc { get; init; }

    public required DateTimeOffset EndTimeUtc { get; init; }

    public required List<FlightObservation> Observations { get; init; }

    public int ObservationCount => Observations.Count;
}
