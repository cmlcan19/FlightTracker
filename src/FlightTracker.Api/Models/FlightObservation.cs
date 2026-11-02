namespace FlightTracker.Api.Models;

public sealed class FlightObservation
{
    public required string Icao24 { get; init; }

    public string? Callsign { get; init; }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public DateTimeOffset? PositionUpdatedAtUtc { get; init; }

    public DateTimeOffset ReceivedAtUtc { get; init; }

    public double? BarometricAltitudeMeters { get; init; }

    public double? GroundSpeedMetersPerSecond { get; init; }

    public double? TrackDegrees { get; init; }

    public bool? OnGround { get; init; }
}