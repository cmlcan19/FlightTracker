using FlightTracker.Api.Models;
using Npgsql;
using NpgsqlTypes;

namespace FlightTracker.Api.Services;

public sealed class FlightQueryRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public FlightQueryRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    
public async Task<List<FlightObservation>> GetLatestAsync(
    bool onlyRecent,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT DISTINCT ON (icao24)
            icao24,
            callsign,
            latitude,
            longitude,
            position_updated_at_utc,
            received_at_utc,
            barometric_altitude_meters,
            ground_speed_meters_per_second,
            track_degrees,
            on_ground
        FROM flight_observations
        WHERE
            NOT $1
            OR position_updated_at_utc >= NOW() - INTERVAL '10 minutes'
        ORDER BY
            icao24,
            position_updated_at_utc DESC NULLS LAST,
            id DESC;
        """;

    await using var command = _dataSource.CreateCommand(sql);

    command.Parameters.AddWithValue(
        NpgsqlDbType.Boolean,
        onlyRecent);

    return await ReadAsync(command, cancellationToken);
}


    
public async Task<List<FlightObservation>> GetHistoryAsync(
    string icao24,
    int limit,
    bool sessionOnly,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT
            icao24,
            callsign,
            latitude,
            longitude,
            position_updated_at_utc,
            received_at_utc,
            barometric_altitude_meters,
            ground_speed_meters_per_second,
            track_degrees,
            on_ground
        FROM flight_observations
        WHERE icao24 = $1
        ORDER BY
            position_updated_at_utc DESC NULLS LAST,
            id DESC
        LIMIT $2;
        """;

    await using var command = _dataSource.CreateCommand(sql);

    command.Parameters.AddWithValue(
        NpgsqlDbType.Varchar, icao24);

    command.Parameters.AddWithValue(
        NpgsqlDbType.Integer, limit);

    var observations = await ReadAsync(
        command, cancellationToken);

    // Eski API davranisini koru.
    if (!sessionOnly)
        return observations;

    var session = new List<FlightObservation>();

    var maxGap = TimeSpan.FromMinutes(15);

    DateTimeOffset? previousTime = null;
    string? sessionCallsign = null;

    foreach (var observation in observations)
    {
        var currentTime = observation.PositionUpdatedAtUtc;

        // Zamani bilinmeyen kayitla rota kurma.
        if (currentTime is null)
            break;

        if (previousTime.HasValue)
        {
            var gap = previousTime.Value - currentTime.Value;

            if (gap < TimeSpan.Zero || gap > maxGap)
                break;
        }

        var currentCallsign = observation.Callsign?.Trim();

        // Bilinen cagri kodu degistiyse yeni oturumdur.
        if (!string.IsNullOrWhiteSpace(sessionCallsign)
            && !string.IsNullOrWhiteSpace(currentCallsign)
            && !string.Equals(
                sessionCallsign,
                currentCallsign,
                StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        session.Add(observation);

        previousTime = currentTime.Value;

        if (!string.IsNullOrWhiteSpace(currentCallsign))
            sessionCallsign = currentCallsign;
    }

    return session;
}

public async Task<List<FlightSession>> GetSessionsAsync(
    string icao24,
    int limit,
    CancellationToken cancellationToken)
{
    var history = await GetHistoryAsync(
        icao24,
        limit,
        false,
        cancellationToken);

    var sessions = new List<FlightSession>();
    var current = new List<FlightObservation>();

    DateTimeOffset? previousTime = null;
    string? currentCallsign = null;

    var maxGap = TimeSpan.FromMinutes(15);

    void SaveSession()
    {
        if (current.Count == 0)
            return;

        sessions.Add(new FlightSession
        {
            Icao24 = icao24,
            Callsign = currentCallsign,
            StartTimeUtc =
                current[^1].PositionUpdatedAtUtc!.Value,
            EndTimeUtc =
                current[0].PositionUpdatedAtUtc!.Value,
            Observations = new List<FlightObservation>(current)
        });

        current.Clear();
        previousTime = null;
        currentCallsign = null;
    }

    foreach (var observation in history)
    {
        var time = observation.PositionUpdatedAtUtc;

        if (!time.HasValue)
        {
            SaveSession();
            continue;
        }

        var callsign = string.IsNullOrWhiteSpace(
            observation.Callsign)
            ? null
            : observation.Callsign.Trim();

        var gapDetected =
            previousTime.HasValue &&
            (
                previousTime.Value < time.Value ||
                previousTime.Value - time.Value > maxGap
            );

        var callsignChanged =
            current.Count > 0 &&
            currentCallsign is not null &&
            callsign is not null &&
            !string.Equals(
                currentCallsign,
                callsign,
                StringComparison.OrdinalIgnoreCase);

        if (gapDetected || callsignChanged)
        {
            SaveSession();
        }

        current.Add(observation);
        previousTime = time.Value;

        currentCallsign ??= callsign;
    }

    SaveSession();

    return sessions;
}


public async Task<List<FlightObservation>> GetArchiveAsync(
    DateTimeOffset fromUtc,
    DateTimeOffset toUtc,
    int limit,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT
            icao24,
            callsign,
            latitude,
            longitude,
            position_updated_at_utc,
            received_at_utc,
            barometric_altitude_meters,
            ground_speed_meters_per_second,
            track_degrees,
            on_ground
        FROM (
            SELECT DISTINCT ON (icao24)
                icao24,
                callsign,
                latitude,
                longitude,
                position_updated_at_utc,
                received_at_utc,
                barometric_altitude_meters,
                ground_speed_meters_per_second,
                track_degrees,
                on_ground,
                id
            FROM flight_observations
            WHERE
                position_updated_at_utc >= $1
                AND position_updated_at_utc < $2
            ORDER BY
                icao24,
                position_updated_at_utc DESC,
                id DESC
        ) AS archived
        ORDER BY
            position_updated_at_utc DESC,
            icao24
        LIMIT $3;
        """;

    await using var command =
        _dataSource.CreateCommand(sql);

    command.Parameters.AddWithValue(
        NpgsqlDbType.TimestampTz,
        fromUtc.ToUniversalTime());

    command.Parameters.AddWithValue(
        NpgsqlDbType.TimestampTz,
        toUtc.ToUniversalTime());

    command.Parameters.AddWithValue(
        NpgsqlDbType.Integer,
        limit);

    return await ReadAsync(
        command,
        cancellationToken);
}

    private static async Task<List<FlightObservation>> ReadAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var flights = new List<FlightObservation>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            flights.Add(new FlightObservation
{
    Icao24 = reader.GetString(0),

    Callsign = reader.IsDBNull(1)
        ? null
        : reader.GetString(1),

    Latitude = reader.GetDouble(2),
    Longitude = reader.GetDouble(3),

    PositionUpdatedAtUtc = reader.IsDBNull(4)
        ? null
        : new DateTimeOffset(reader.GetDateTime(4)),

    ReceivedAtUtc =
        new DateTimeOffset(reader.GetDateTime(5)),

    BarometricAltitudeMeters = reader.IsDBNull(6)
        ? null
        : reader.GetDouble(6),

    GroundSpeedMetersPerSecond = reader.IsDBNull(7)
        ? null
        : reader.GetDouble(7),

    TrackDegrees = reader.IsDBNull(8)
        ? null
        : reader.GetDouble(8),

    OnGround = reader.IsDBNull(9)
        ? null
        : reader.GetBoolean(9)
});
        }

        return flights;
    }
}