using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class PublicSitePageTests
{
    [Fact]
    public void Landing_page_is_a_public_presentation_with_intranet_links()
    {
        var root = FindWwwRoot();
        var html = File.ReadAllText(Path.Combine(root, "landing.html"));
        var css = File.ReadAllText(Path.Combine(root, "landing.css"));
        var webConfig = File.ReadAllText(Path.Combine(root, "web.config"));
        var index = File.ReadAllText(Path.Combine(root, "index.html"));

        Assert.Contains("lang=\"cs\"", html, StringComparison.Ordinal);
        Assert.Contains("erp.css?v=6", html, StringComparison.Ordinal);
        Assert.Contains("landing.css?v=1", html, StringComparison.Ordinal);
        Assert.Contains("class=\"module-links\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Intranet\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"intranet-label\">Intranet</span>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://erp.phmtrans.cz/\">ERP</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://disp.phmtrans.cz/\">Dispečink</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://crm.phmtrans.cz/\">CRM</a>", html, StringComparison.Ordinal);
        Assert.Contains("<h1 id=\"hero-title\">Silniční nákladní doprava</h1>", html, StringComparison.Ordinal);
        Assert.Contains("Vnitrostátní a mezinárodní kamionová doprava", html, StringComparison.Ordinal);
        Assert.Contains("Přeprava PHM a cisterny", html, StringComparison.Ordinal);
        Assert.Contains(">Spedice</h3>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"vozovy-park\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"o-nas\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"kontakt\"", html, StringComparison.Ordinal);
        Assert.Contains("[telefon]", html, StringComparison.Ordinal);
        Assert.Contains("[e-mail]", html, StringComparison.Ordinal);
        Assert.Contains("[adresa]", html, StringComparison.Ordinal);
        Assert.Contains("UPRAVIT: text o firmě", html, StringComparison.Ordinal);
        Assert.Contains("UPRAVIT: popis vozového parku", html, StringComparison.Ordinal);
        Assert.Contains("[Doplňte text o firmě.]", html, StringComparison.Ordinal);
        Assert.Contains("[Doplňte popis vozového parku.]", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@", html, StringComparison.Ordinal);
        Assert.DoesNotContain("+420", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html.Replace("https://erp.phmtrans.cz/", "", StringComparison.Ordinal)
            .Replace("https://disp.phmtrans.cz/", "", StringComparison.Ordinal)
            .Replace("https://crm.phmtrans.cz/", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("google", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("var(--accent)", css, StringComparison.Ordinal);
        Assert.Contains("var(--button)", css, StringComparison.Ordinal);

        Assert.Contains("name=\"PublicHome\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"PublicIndex\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("^(www\\.)?phmtrans\\.cz(?::\\d+)?$", webConfig, StringComparison.Ordinal);
        Assert.Contains("url=\"landing.html\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"ErpHome\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"DispHome\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"CrmHome\"", webConfig, StringComparison.Ordinal);

        Assert.Contains("https://disp.phmtrans.cz/", index, StringComparison.Ordinal);
        Assert.Contains("https://crm.phmtrans.cz/", index, StringComparison.Ordinal);
        Assert.DoesNotContain("Intranet", index, StringComparison.Ordinal);
        Assert.DoesNotContain("landing.html", index, StringComparison.Ordinal);
    }

    private static string FindWwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var wwwroot = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(wwwroot, "landing.html")))
                return wwwroot;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

public sealed class PublicSiteHttpTests : IClassFixture<PublicSiteApp>
{
    private readonly PublicSiteApp _app;

    public PublicSiteHttpTests(PublicSiteApp app) => _app = app;

    [Theory]
    [InlineData("phmtrans.cz")]
    [InlineData("www.phmtrans.cz")]
    [InlineData("WWW.PHMTRANS.CZ")]
    [InlineData("phmtrans.cz:443")]
    public async Task Public_hosts_serve_the_landing_page_without_login(string host)
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in new[] { "/", "/index.html" })
        {
            var response = await client.SendAsync(Request(path, user: false, host));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Headers.Location);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Silniční nákladní doprava", html, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"Intranet\"", html, StringComparison.Ordinal);
            Assert.Contains("https://erp.phmtrans.cz/", html, StringComparison.Ordinal);
            Assert.Contains("[telefon]", html, StringComparison.Ordinal);
            Assert.DoesNotContain("src=\"/image.jpg\"", html, StringComparison.Ordinal);
        }

        var signed = await client.SendAsync(Request("/", user: true, host));
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var signedHtml = await signed.Content.ReadAsStringAsync();
        Assert.Contains("id=\"kontakt\"", signedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>ERP</h1>", signedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Dispečink</h1>", signedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>CRM</h1>", signedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_host_keeps_login_health_and_does_not_steal_module_hosts()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var login = await client.SendAsync(Request("/login.html", user: false, "www.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginHtml = await login.Content.ReadAsStringAsync();
        Assert.Contains("Přihlášení", loginHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("[telefon]", loginHtml, StringComparison.Ordinal);

        var direct = await client.SendAsync(Request("/landing.html", user: false, "localhost"));
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.Contains("Vozový park", await direct.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var health = await client.SendAsync(Request("/health", user: false, "www.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("json", health.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());

        var portal = await client.SendAsync(Request("/", user: false, host: null));
        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        var portalHtml = await portal.Content.ReadAsStringAsync();
        Assert.Contains("src=\"/image.jpg\"", portalHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Intranet\"", portalHtml, StringComparison.Ordinal);

        foreach (var host in new[] { "erp.phmtrans.cz", "www.erp.phmtrans.cz", "disp.phmtrans.cz", "www.disp.phmtrans.cz", "crm.phmtrans.cz", "www.crm.phmtrans.cz" })
        {
            var home = await client.SendAsync(Request("/", user: false, host));
            Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
            Assert.Equal("/login.html", home.Headers.Location?.OriginalString);
        }

        var signedErp = await client.SendAsync(Request("/", user: true, "erp.phmtrans.cz"));
        Assert.Contains("Vydané faktury", await signedErp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var signedDispatch = await client.SendAsync(Request("/", user: true, "disp.phmtrans.cz"));
        Assert.Contains("<h1>Dispečink</h1>", await signedDispatch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var signedCrm = await client.SendAsync(Request("/", user: true, "crm.phmtrans.cz"));
        Assert.Contains("<h1>CRM</h1>", await signedCrm.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static HttpRequestMessage Request(string path, bool user, string? host)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, path);
        if (host is not null)
            message.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        if (user)
            message.Headers.TryAddWithoutValidation("X-Test-User", "uctarna");
        return message;
    }
}

public sealed class PublicSiteApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindContentRoot());
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PublicSiteAuthHandler>("Test", _ => { });
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
            if (File.Exists(Path.Combine(dir.FullName, "wwwroot", "landing.html")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

file sealed class PublicSiteAuthHandler(
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
