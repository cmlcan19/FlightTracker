using FlightTracker.Consumer.Models;
using Npgsql;
using NpgsqlTypes;

namespace FlightTracker.Consumer.Services;

public sealed class FlightObservationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public FlightObservationRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<bool> SaveAsync(
        FlightObservation flight,
        string topic,
        int partition,
        long offset,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO flight_observations
            (
                icao24,
                callsign,
                latitude,
                longitude,
                position_updated_at_utc,
                kafka_topic,
                kafka_partition,
                kafka_offset,
                barometric_altitude_meters,
                ground_speed_meters_per_second,
                track_degrees,
                on_ground
            )
            VALUES
            (
                $1, $2, $3, $4, $5, $6,
                $7, $8, $9, $10, $11, $12
            )
            ON CONFLICT (kafka_topic, kafka_partition, kafka_offset)
            DO NOTHING;
            """;

        await using var command = _dataSource.CreateCommand(sql);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Varchar, flight.Icao24);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            (object?)flight.Callsign ?? DBNull.Value);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Double, flight.Latitude);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Double, flight.Longitude);

        command.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            (object?)flight.PositionUpdatedAtUtc?.UtcDateTime
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Text, topic);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Integer, partition);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Bigint, offset);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Double,
            (object?)flight.BarometricAltitudeMeters
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Double,
            (object?)flight.GroundSpeedMetersPerSecond
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Double,
            (object?)flight.TrackDegrees
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            NpgsqlDbType.Boolean,
            (object?)flight.OnGround ?? DBNull.Value);

        var affectedRows = await command.ExecuteNonQueryAsync(
            cancellationToken);

        return affectedRows == 1;
    }
}