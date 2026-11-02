using FlightTracker.Consumer;
using FlightTracker.Consumer.Services;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Postgres");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Postgres bağlantı ayarı bulunamadı.");
}

builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    NpgsqlDataSource.Create(connectionString));

builder.Services.AddSingleton<FlightObservationRepository>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();