using FlightTracker.Producer;
using FlightTracker.Producer.Services;

var builder = Host.CreateApplicationBuilder(args);

var baseUrl = builder.Configuration["OpenSky:BaseUrl"]
    ?? throw new InvalidOperationException(
        "OpenSky:BaseUrl ayarı bulunamadı.");

var timeoutSeconds = builder.Configuration.GetValue<int>(
    "OpenSky:TimeoutSeconds");

if (timeoutSeconds <= 0)
{
    throw new InvalidOperationException(
        "OpenSky:TimeoutSeconds sıfırdan büyük olmalıdır.");
}

builder.Services.AddHttpClient("OpenSky", client =>
{
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});

builder.Services.AddSingleton<OpenSkyClient>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<KafkaProducer>();

var host = builder.Build();
host.Run();