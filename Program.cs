using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using PHMTRANS.Bi;
using PHMTRANS.Cpq;
using PHMTRANS.Crm;
using PHMTRANS.Dispatch;
using PHMTRANS.Pohoda;
using PHMTRANS.Site;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var siteWebRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "wwwroot"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Directory.Exists(siteWebRoot) ? siteWebRoot : null
});

builder.Host.UseWindowsService();

if (!builder.Environment.IsDevelopment())
    builder.WebHost.UseUrls("http://*:8089");

builder.Services.AddOpenApi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "phmtrans-auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login.html";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.Configure<PohodaOptions>(builder.Configuration.GetSection(PohodaOptions.SectionName));
builder.Services.AddSingleton<IssuedInvoiceCache>();
builder.Services.AddSingleton<SqlDispatchStore>();
builder.Services.AddSingleton<IDispatchStore>(sp => sp.GetRequiredService<SqlDispatchStore>());
builder.Services.AddHostedService<DispatchSchemaInitializer>();
builder.Services.AddSingleton<SqlCrmStore>();
builder.Services.AddSingleton<ICrmStore>(sp => sp.GetRequiredService<SqlCrmStore>());
builder.Services.AddHostedService<CrmSchemaInitializer>();
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
app.UseAuthentication();
app.UseAuthorization();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapErpAuth();
app.MapDispatch();
app.MapCrm();
app.MapBi();
app.MapPublicSite();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapCpq();

app.MapGet("/health", () =>
{
    var assembly = Assembly.GetEntryAssembly();
    var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly?.GetName().Version?.ToString()
        ?? "unknown";

    return new HealthResponse("ok", version, DateTime.UtcNow, ReadGitBranch(assembly));
});

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

static string ReadGitBranch(Assembly? assembly)
{
    var fromEnvironment = Environment.GetEnvironmentVariable("GIT_BRANCH");
    if (!string.IsNullOrWhiteSpace(fromEnvironment))
        return fromEnvironment.Trim();

    var fromAssembly = assembly?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "GitBranch")
        ?.Value;

    return string.IsNullOrWhiteSpace(fromAssembly) ? "unknown" : fromAssembly.Trim();
}

app.Run();

record HealthResponse(string Status, string Version, DateTime TimeUtc, string Branch);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

public partial class Program;
