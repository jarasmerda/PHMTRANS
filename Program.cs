using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using PHMTRANS.Bi;
using PHMTRANS.Cpq;
using PHMTRANS.Pohoda;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.Configure<PohodaOptions>(builder.Configuration.GetSection(PohodaOptions.SectionName));
builder.Services.AddHttpClient<PohodaMServerClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false })
    .ConfigureHttpClient((services, client) =>
    {
        var pohoda = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PohodaOptions>>().Value;
        client.BaseAddress = new Uri($"{pohoda.BaseUrl.TrimEnd('/')}/");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(pohoda.TimeoutSeconds, 5, 600));
        client.DefaultRequestHeaders.ExpectContinue = false;
    });

var app = builder.Build();

app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapBi();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapCpq();

app.MapGet("/vlastovka", () => Results.Content("""
    <!DOCTYPE html>
    <html lang="cs">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>Vlaštovka</title>
      <style>
        body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #eef3f8; }
        img { width: min(960px, 100%); height: auto; display: block; }
      </style>
    </head>
    <body>
      <img src="/vlastovka.jpg" alt="Vlaštovka">
    </body>
    </html>
    """, "text/html; charset=utf-8"));

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
