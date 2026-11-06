using System.Net;
using Xunit;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHMTRANS.Dispatch;

namespace PHMTRANS.Tests;

public sealed class DispatchHttpTests : IClassFixture<DispatchApp>
{
    private readonly DispatchApp _app;

    public DispatchHttpTests(DispatchApp app) => _app = app;

    [Fact]
    public async Task Dispatch_host_opens_the_board_and_erp_host_stays_on_invoices()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var board = await client.SendAsync(Request("/api/dispatch/board", "intranet.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Unauthorized, board.StatusCode);
        Assert.Contains("Přihlaste se", await board.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var dispatchHome = await client.SendAsync(Request("/", "intranet.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Redirect, dispatchHome.StatusCode);
        Assert.Equal("/login.html", dispatchHome.Headers.Location?.OriginalString);

        var erpHome = await client.SendAsync(Request("/", "erp.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.MovedPermanently, erpHome.StatusCode);
        Assert.Equal("https://intranet.phmtrans.cz/ERP", erpHome.Headers.Location?.OriginalString);

        var portal = await client.SendAsync(Request("/", null, user: false));
        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        Assert.Contains("https://intranet.phmtrans.cz/\">Intranet</a>", await portal.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var signedDispatch = await client.SendAsync(Request("/Dispecink", "intranet.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.OK, signedDispatch.StatusCode);
        var dispatchHtml = await signedDispatch.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Dispečink</h1>", dispatchHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Vydané faktury", dispatchHtml, StringComparison.Ordinal);

        var signedErp = await client.SendAsync(Request("/ERP", "intranet.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.OK, signedErp.StatusCode);
        var erpHtml = await signedErp.Content.ReadAsStringAsync();
        Assert.Contains("Vydané faktury", erpHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Dispečink</h1>", erpHtml, StringComparison.Ordinal);

        var data = await client.SendAsync(Request("/api/dispatch/board", "intranet.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        using var json = JsonDocument.Parse(await data.Content.ReadAsStringAsync());
        Assert.Equal(DispatchStorage.Kind, json.RootElement.GetProperty("storage").GetString());
        Assert.Equal("sql", json.RootElement.GetProperty("storage").GetString());
        Assert.Contains("Pohody", json.RootElement.GetProperty("storageNote").GetString(), StringComparison.Ordinal);
        Assert.False(json.RootElement.TryGetProperty("trips", out _));
        Assert.Equal(3, json.RootElement.GetProperty("drivers").GetArrayLength());
    }



    [Fact]
    public async Task Saving_a_distance_needs_login_and_valid_kilometers()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpRequestMessage Post(string path, string body, bool user)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
            if (user)
                message.Headers.TryAddWithoutValidation("X-Test-User", "dispecer");
            return message;
        }

        var anonymous = await client.SendAsync(Post("/api/dispatch/distances", """{"from":"Sklad Test","to":"ČS Test","km":42}""", user: false));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var invalid = await client.SendAsync(Post("/api/dispatch/distances", """{"from":"Sklad Test","to":"ČS Test","km":0}""", user: true));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var json = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.Contains("Kilometry", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Documents_need_login_and_explain_a_missing_folder()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var anonymous = await client.SendAsync(Request("/api/dispatch/orders/x/documents", "intranet.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var list = await client.SendAsync(Request("/api/dispatch/orders/x/documents", "intranet.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("configured").GetBoolean());
        Assert.Contains("Dispatch:DocumentsPath", json.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calendar_needs_login_and_a_valid_range_and_trips_are_gone()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var anonymous = await client.SendAsync(Request("/api/dispatch/calendar", "intranet.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var invalid = await client.SendAsync(Request("/api/dispatch/calendar?days=500", "intranet.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var trips = await client.SendAsync(Request("/api/dispatch/trips", "intranet.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.NotFound, trips.StatusCode);
    }

    private static HttpRequestMessage Put(string path, string json, bool user = true)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = Json(json)
        };
        if (user)
            message.Headers.TryAddWithoutValidation("X-Test-User", "dispecer");
        return message;
    }

    private static StringContent Json(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    private static HttpRequestMessage Request(string path, string? host, bool user)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, path);
        if (host is not null)
            message.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        if (user)
            message.Headers.TryAddWithoutValidation("X-Test-User", "dispecer");
        return message;
    }
}

public sealed class DispatchApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindContentRoot());
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            });
            foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IDispatchStore)).ToList())
                services.Remove(descriptor);
            services.AddSingleton<IDispatchStore, MemoryDispatchStore>();
        });
    }

    private static string FindContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "wwwroot", "dispecink.html")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

public sealed class DispatchWithoutDatabaseTests : IClassFixture<DispatchBareApp>
{
    private readonly DispatchBareApp _app;

    public DispatchWithoutDatabaseTests(DispatchBareApp app) => _app = app;

    [Fact]
    public async Task Health_stays_up_and_dispatch_explains_the_missing_connection_string()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var anonymous = await client.GetAsync("/api/dispatch/board");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var board = new HttpRequestMessage(HttpMethod.Get, "/api/dispatch/board");
        board.Headers.TryAddWithoutValidation("X-Test-User", "dispecer");
        var response = await client.SendAsync(board);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("ConnectionStrings:Dispatch", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

public sealed class DispatchBareApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindContentRoot());
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            });
        });
    }

    private static string FindContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "wwwroot", "dispecink.html")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

file sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-User"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = TestUsers.Identity(Request, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
