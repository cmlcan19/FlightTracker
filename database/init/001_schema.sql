BEGIN;

CREATE TABLE IF NOT EXISTS flight_observations
(
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    icao24 VARCHAR(6) NOT NULL,
    callsign TEXT,
    latitude DOUBLE PRECISION NOT NULL,
    longitude DOUBLE PRECISION NOT NULL,
    position_updated_at_utc TIMESTAMPTZ,
    received_at_utc TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    kafka_topic TEXT NOT NULL,
    kafka_partition INTEGER NOT NULL,
    kafka_offset BIGINT NOT NULL,
    barometric_altitude_meters DOUBLE PRECISION,
    ground_speed_meters_per_second DOUBLE PRECISION,
    track_degrees DOUBLE PRECISION,
    on_ground BOOLEAN,

    CONSTRAINT flight_observations_latitude_check
        CHECK (latitude BETWEEN -90 AND 90),

    CONSTRAINT flight_observations_longitude_check
        CHECK (longitude BETWEEN -180 AND 180),

    CONSTRAINT uq_flight_observations_kafka_message
        UNIQUE (kafka_topic, kafka_partition, kafka_offset)
);

CREATE INDEX IF NOT EXISTS ix_flight_observations_aircraft_time
    ON flight_observations
    (icao24, position_updated_at_utc DESC);

COMMIT;