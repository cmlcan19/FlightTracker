using System.Text.RegularExpressions;
using FlightTracker.Api.Hubs;
using FlightTracker.Api.Services;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Postgres");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Postgres bağlantı ayarı bulunamadı.");
}

builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    NpgsqlDataSource.Create(connectionString));

builder.Services.AddSingleton<FlightQueryRepository>();

builder.Services.AddSignalR();
builder.Services.AddHostedService<LiveFlightConsumer>();

var app = builder.Build();

app.MapHub<FlightsHub>("/hubs/flights");


app.MapGet("/api/flights/latest", async (
    bool? onlyRecent,
    FlightQueryRepository repository,
    CancellationToken cancellationToken) =>
{
    var flights = await repository.GetLatestAsync(
        onlyRecent ?? false,
        cancellationToken);

    return Results.Ok(flights);
});



app.MapGet("/api/flights/{icao24}/history", async (
    string icao24,
    int? limit,
    bool? sessionOnly,
    FlightQueryRepository repository,
    CancellationToken cancellationToken) =>
{
    if (!Regex.IsMatch(icao24, @"\A[0-9a-fA-F]{6}\z"))
    {
        return Results.BadRequest(new
        {
            error = "Uçak kimliği 6 hexadecimal karakter olmalıdır."
        });
    }

    var recordLimit = limit ?? 100;

    if (recordLimit < 1 || recordLimit > 1000)
    {
        return Results.BadRequest(new
        {
            error = "limit değeri 1 ile 1000 arasında olmalıdır."
        });
    }

    var flights = await repository.GetHistoryAsync(
        icao24.ToLowerInvariant(),
        recordLimit,
        sessionOnly ?? false,
        cancellationToken);

    return Results.Ok(flights);
});

app.MapGet("/api/flights/{icao24}/sessions", async (
    string icao24,
    int? limit,
    FlightQueryRepository repository,
    CancellationToken cancellationToken) =>
{
    if (!Regex.IsMatch(icao24, @"\A[0-9a-fA-F]{6}\z"))
    {
        return Results.BadRequest(new
        {
            error = "Gecersiz ICAO24 kodu."
        });
    }

    var recordLimit = limit ?? 500;

    if (recordLimit < 1 || recordLimit > 1000)
    {
        return Results.BadRequest(new
        {
            error = "Limit 1 ile 1000 arasinda olmalidir."
        });
    }

    var sessions = await repository.GetSessionsAsync(
        icao24.ToLowerInvariant(),
        recordLimit,
        cancellationToken);

    return Results.Ok(sessions);
});


app.MapGet("/api/flights/archive", async (
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? limit,
    FlightQueryRepository repository,
    CancellationToken cancellationToken) =>
{
    if (!fromUtc.HasValue || !toUtc.HasValue)
    {
        return Results.BadRequest(new
        {
            error = "fromUtc ve toUtc zorunludur."
        });
    }

    var start = fromUtc.Value.ToUniversalTime();
    var end = toUtc.Value.ToUniversalTime();

    if (start >= end)
    {
        return Results.BadRequest(new
        {
            error = "Baslangic tarihi bitisten once olmalidir."
        });
    }

    if (end - start > TimeSpan.FromDays(7))
    {
        return Results.BadRequest(new
        {
            error = "En fazla 7 gunluk aralik sorgulanabilir."
        });
    }

    var recordLimit = limit ?? 500;

    if (recordLimit < 1 || recordLimit > 1000)
    {
        return Results.BadRequest(new
        {
            error = "Limit 1 ile 1000 arasinda olmalidir."
        });
    }

    var flights = await repository.GetArchiveAsync(
        start,
        end,
        recordLimit,
        cancellationToken);

    return Results.Ok(flights);
});

app.Run();