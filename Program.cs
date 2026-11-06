using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using PHMTRANS.Auth;
using PHMTRANS.Bi;
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

// Služba je za IIS/ARR na stejném serveru, proto poslouchá jen na loopbacku.
// Jinou adresu jde nastavit přes Urls (proměnná ASPNETCORE_URLS).
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:8089");

builder.Services.Configure<IntranetOptions>(builder.Configuration.GetSection(IntranetOptions.SectionName));
builder.Services.AddSingleton<IPasswordHasher<AuthUser>, PasswordHasher<AuthUser>>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => IntranetCookies.Apply(options, builder.Environment.IsDevelopment()));
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Hlavičky X-Forwarded-* se berou jen od důvěryhodné proxy. Výchozí je loopback (IIS/ARR na stejném serveru),
    // další adresy jdou doplnit v ForwardedHeaders:KnownProxies.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        if (System.Net.IPAddress.TryParse(proxy, out var address))
            options.KnownProxies.Add(address);
    }
});
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));
builder.Services.Configure<PohodaOptions>(builder.Configuration.GetSection(PohodaOptions.SectionName));
builder.Services.AddSingleton<IssuedInvoiceCache>();
builder.Services.AddSingleton<SqlDispatchStore>();
builder.Services.AddSingleton<IDispatchStore>(sp => sp.GetRequiredService<SqlDispatchStore>());
builder.Services.AddHostedService<DispatchSchemaInitializer>();
builder.Services.AddSingleton<SqlOrderStore>();
builder.Services.AddSingleton<IOrderStore>(sp => sp.GetRequiredService<SqlOrderStore>());
builder.Services.AddHostedService<OrderSyncWorker>();
builder.Services.AddSingleton<SqlCrmStore>();
builder.Services.AddSingleton<ICrmStore>(sp => sp.GetRequiredService<SqlCrmStore>());
builder.Services.AddHostedService<CrmSchemaInitializer>();
builder.Services.AddSingleton<SqlAuthStore>();
builder.Services.AddSingleton<IAuthStore>(sp => sp.GetRequiredService<SqlAuthStore>());
builder.Services.AddHostedService<AuthSchemaInitializer>();
builder.Services.AddSingleton<SqlPohodaAddressBook>();
builder.Services.AddSingleton<IPohodaAddressBook>(sp => sp.GetRequiredService<SqlPohodaAddressBook>());
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
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseSecurityHeaders();
app.UseHostRouting();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseModuleGate();

app.MapAuth();
app.MapDispatch();
app.MapOrders();
app.MapCrm();
app.MapBi();
app.MapPublicSite();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () =>
{
    var assembly = Assembly.GetEntryAssembly();
    var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly?.GetName().Version?.ToString()
        ?? "unknown";

    return new HealthResponse("ok", version, DateTime.UtcNow, ReadGitBranch(assembly));
});

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

public partial class Program;
