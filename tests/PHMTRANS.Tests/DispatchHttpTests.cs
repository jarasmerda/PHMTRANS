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

        var board = await client.SendAsync(Request("/api/dispatch/board", "disp.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Unauthorized, board.StatusCode);
        Assert.Contains("Přihlaste se", await board.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var dispatchHome = await client.SendAsync(Request("/", "disp.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Redirect, dispatchHome.StatusCode);
        Assert.Equal("/login.html", dispatchHome.Headers.Location?.OriginalString);

        var erpHome = await client.SendAsync(Request("/", "erp.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Redirect, erpHome.StatusCode);
        Assert.Equal("/login.html", erpHome.Headers.Location?.OriginalString);

        var portal = await client.SendAsync(Request("/", null, user: false));
        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        Assert.Contains("https://disp.phmtrans.cz/", await portal.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var signedDispatch = await client.SendAsync(Request("/", "www.disp.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.OK, signedDispatch.StatusCode);
        var dispatchHtml = await signedDispatch.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Dispečink</h1>", dispatchHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Vydané faktury", dispatchHtml, StringComparison.Ordinal);

        var signedErp = await client.SendAsync(Request("/", "erp.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.OK, signedErp.StatusCode);
        var erpHtml = await signedErp.Content.ReadAsStringAsync();
        Assert.Contains("Vydané faktury", erpHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Dispečink</h1>", erpHtml, StringComparison.Ordinal);

        var data = await client.SendAsync(Request("/api/dispatch/board", "disp.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        using var json = JsonDocument.Parse(await data.Content.ReadAsStringAsync());
        Assert.Equal(DispatchStore.StorageKind, json.RootElement.GetProperty("storage").GetString());
        Assert.Contains("Dočasné", json.RootElement.GetProperty("storageNote").GetString(), StringComparison.Ordinal);
        Assert.True(json.RootElement.GetProperty("trips").GetArrayLength() >= 5);

        var trip = json.RootElement.GetProperty("trips").EnumerateArray()
            .Single(item => item.GetProperty("status").GetString() == DispatchStatuses.Nova);
        var assign = new HttpRequestMessage(HttpMethod.Post, $"/api/dispatch/trips/{trip.GetProperty("id").GetString()}/assign")
        {
            Content = new StringContent(
                """{"vehicleId":"veh-1","driverId":"drv-1"}""",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        assign.Headers.TryAddWithoutValidation("X-Test-User", "dispecer");
        var assigned = await client.SendAsync(assign);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        using var assignedJson = JsonDocument.Parse(await assigned.Content.ReadAsStringAsync());
        Assert.Equal(DispatchStatuses.Prirazena, assignedJson.RootElement.GetProperty("status").GetString());
    }

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
    private readonly string _storeDirectory = Directory.CreateTempSubdirectory("phmtrans-dispatch-http").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Dispatch:StorePath", Path.Combine(_storeDirectory, "store.json"));
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_storeDirectory))
            Directory.Delete(_storeDirectory, recursive: true);
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

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, Request.Headers["X-Test-User"].ToString())],
            "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}
