using System.Buffers.Binary;
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
        Assert.Contains("erp.css?v=17", html, StringComparison.Ordinal);
        Assert.Contains("landing.css?v=14", html, StringComparison.Ordinal);
        Assert.Contains("class=\"module-links\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Intranet\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://intranet.phmtrans.cz/\">Intranet</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"intranet-label\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://intranet.phmtrans.cz/ERP", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://intranet.phmtrans.cz/Dispecink", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://intranet.phmtrans.cz/CRM", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">ERP</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Dispečink</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">CRM</a>", html, StringComparison.Ordinal);
        Assert.Contains("<h1 id=\"hero-title\">Pohonné hmoty<br><span>v pohybu</span></h1>", html, StringComparison.Ordinal);
        Assert.Contains("Silniční nákladní doprava", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Kamionová doprava</h3>", html, StringComparison.Ordinal);
        Assert.Contains("mezinárodní silniční dopravě", html, StringComparison.Ordinal);
        Assert.Contains("Přeprava PHM a cisterny", html, StringComparison.Ordinal);
        Assert.Contains(">Spedice</h3>", html, StringComparison.Ordinal);
        Assert.Contains("srcset=\"/landing/logo-dark.png 640w\"", html, StringComparison.Ordinal);
        Assert.Contains("alt=\"PHM TRANS\"", html, StringComparison.Ordinal);
        Assert.Contains("alt=\"Cisterna Scania pro přepravu pohonných hmot\"", html, StringComparison.Ordinal);
        Assert.Contains("loading=\"lazy\"", html, StringComparison.Ordinal);
        Assert.Contains("fetchpriority=\"high\"", html, StringComparison.Ordinal);
        Assert.Contains("for=\"nav-toggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"vozovy-park\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"o-nas\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"kontakt\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"eu-projekt\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[telefon]", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[e-mail]", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[adresa]", html, StringComparison.Ordinal);
        Assert.Contains("UPRAVIT: text o firmě", html, StringComparison.Ordinal);
        Assert.Contains("UPRAVIT: popis vozového parku", html, StringComparison.Ordinal);
        Assert.Contains("[Doplňte text o firmě.]", html, StringComparison.Ordinal);
        Assert.Contains("[Doplňte popis vozového parku.]", html, StringComparison.Ordinal);
        var contactAt = html.IndexOf("id=\"kontakt\"", StringComparison.Ordinal);
        var euAt = html.IndexOf("id=\"eu-projekt\"", StringComparison.Ordinal);
        var footerAt = html.IndexOf("class=\"site-footer\"", StringComparison.Ordinal);
        Assert.True(contactAt >= 0 && euAt > contactAt && footerAt > euAt);
        const string operatorLine = "Provozovatel: Autodoprava Hrbáček s.r.o.";
        Assert.Contains(operatorLine, html[contactAt..euAt], StringComparison.Ordinal);
        Assert.Contains(operatorLine, html[footerAt..], StringComparison.Ordinal);
        Assert.Contains("href=\"tel:+420603475612\"", html, StringComparison.Ordinal);
        Assert.Contains("+420 603 475 612", html, StringComparison.Ordinal);
        Assert.Contains("href=\"mailto:info@phmtrans.cz\"", html, StringComparison.Ordinal);
        Assert.Contains("info@phmtrans.cz", html, StringComparison.Ordinal);
        Assert.Contains("Nížkovice 41, 684 01 Nížkovice", html, StringComparison.Ordinal);
        Assert.Contains("IČO: 26950430", html, StringComparison.Ordinal);
        Assert.Contains("DIČ: CZ26950430", html, StringComparison.Ordinal);
        Assert.Contains("zapsána v obchodním rejstříku vedeném Krajským soudem v Brně, oddíl C, vložka 47795", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/img/eu-projekt.png\"", html, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener\"", html, StringComparison.Ordinal);
        Assert.Contains("Projekt spolufinancovaný Evropskou unií", html, StringComparison.Ordinal);
        Assert.DoesNotContain("eu-poster", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=\"/img/eu-projekt.png\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("width=\"3840\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<figcaption>", html, StringComparison.Ordinal);
        var jsonStart = html.IndexOf("<script type=\"application/ld+json\">", StringComparison.Ordinal);
        var jsonEnd = html.IndexOf("</script>", jsonStart, StringComparison.Ordinal);
        Assert.True(jsonStart >= 0 && jsonEnd > jsonStart);
        using (var organization = JsonDocument.Parse(html[(jsonStart + "<script type=\"application/ld+json\">".Length)..jsonEnd]))
        {
            var org = organization.RootElement;
            Assert.Equal("https://schema.org", org.GetProperty("@context").GetString());
            Assert.Equal("Organization", org.GetProperty("@type").GetString());
            Assert.Equal("Autodoprava Hrbáček s.r.o.", org.GetProperty("name").GetString());
            Assert.Equal("info@phmtrans.cz", org.GetProperty("email").GetString());
            Assert.Equal("+420603475612", org.GetProperty("telephone").GetString());
            Assert.Equal("CZ26950430", org.GetProperty("vatID").GetString());
            Assert.Equal("26950430", org.GetProperty("identifier").GetProperty("value").GetString());
            Assert.Equal("Nížkovice 41", org.GetProperty("address").GetProperty("streetAddress").GetString());
            Assert.Equal("684 01", org.GetProperty("address").GetProperty("postalCode").GetString());
            Assert.Equal("Nížkovice", org.GetProperty("address").GetProperty("addressLocality").GetString());
            Assert.Contains("oddíl C, vložka 47795", org.GetProperty("additionalProperty").GetProperty("value").GetString(), StringComparison.Ordinal);
        }

        var withoutJsonLd = html.Remove(jsonStart, jsonEnd + "</script>".Length - jsonStart);
        Assert.DoesNotContain("<script", withoutJsonLd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        var external = html
            .Replace("https://intranet.phmtrans.cz/", "", StringComparison.Ordinal)
            .Replace("https://schema.org", "", StringComparison.Ordinal)
            .Replace("https://phmtrans.cz/", "", StringComparison.Ordinal)
            .Replace("https://www.mapaprojektu.cz", "", StringComparison.Ordinal);
        Assert.DoesNotContain("https://", external, StringComparison.Ordinal);
        Assert.DoesNotContain("google", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".eu-poster", css, StringComparison.Ordinal);
        Assert.Contains("height: auto", css, StringComparison.Ordinal);
        var poster = File.ReadAllBytes(Path.Combine(root, "img", "eu-projekt.png"));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, poster[..4]);
        Assert.Equal(3840, BinaryPrimitives.ReadInt32BigEndian(poster.AsSpan(16, 4)));
        Assert.Equal(2160, BinaryPrimitives.ReadInt32BigEndian(poster.AsSpan(20, 4)));
        Assert.True(poster.Length < 200_000);
        Assert.Contains("max-width: 480px", css, StringComparison.Ordinal);
        Assert.Contains("max-width: 768px", css, StringComparison.Ordinal);
        Assert.Contains("max-width: 1024px", css, StringComparison.Ordinal);
        Assert.Contains("min-width: 481px", css, StringComparison.Ordinal);
        Assert.Contains("min-width: 769px", css, StringComparison.Ordinal);
        Assert.Contains("min-width: 1025px", css, StringComparison.Ordinal);
        var urlCount = 0;
        var searchFrom = 0;
        while (css.IndexOf("url(", searchFrom, StringComparison.OrdinalIgnoreCase) is var urlAt and >= 0)
        {
            var end = css.IndexOf(')', urlAt);
            Assert.True(end > urlAt);
            var value = css[(urlAt + 4)..end].Trim().Trim('"').Trim('\'');
            Assert.StartsWith("/landing/fonts/", value, StringComparison.Ordinal);
            Assert.EndsWith(".woff2", value, StringComparison.Ordinal);
            urlCount++;
            searchFrom = end + 1;
        }

        Assert.True(urlCount >= 12);
        Assert.Contains("var(--accent)", css, StringComparison.Ordinal);
        Assert.Contains("var(--button)", css, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "landing", "logo-dark.png")));
        Assert.True(File.Exists(Path.Combine(root, "landing", "tanker-scania.webp")));
        Assert.True(File.Exists(Path.Combine(root, "landing", "truck-white-scania.webp")));
        Assert.True(File.Exists(Path.Combine(root, "landing", "truck-sunset.webp")));

        Assert.Contains("name=\"PublicHome\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"PublicIndex\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"WwwToApex\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("^www\\.phmtrans\\.cz(?::\\d+)?$", webConfig, StringComparison.Ordinal);
        Assert.Contains("^phmtrans\\.cz(?::\\d+)?$", webConfig, StringComparison.Ordinal);
        Assert.Contains("https://phmtrans.cz/{R:0}", webConfig, StringComparison.Ordinal);
        Assert.Contains("url=\"landing.html\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"ErpHome\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"DispHome\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"CrmHome\"", webConfig, StringComparison.Ordinal);

        Assert.Contains("https://intranet.phmtrans.cz/Dispecink", index, StringComparison.Ordinal);
        Assert.Contains("https://intranet.phmtrans.cz/CRM", index, StringComparison.Ordinal);
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
            Assert.Contains("https://intranet.phmtrans.cz/\">Intranet</a>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("https://intranet.phmtrans.cz/ERP", html, StringComparison.Ordinal);
            Assert.DoesNotContain("https://intranet.phmtrans.cz/Dispecink", html, StringComparison.Ordinal);
            Assert.DoesNotContain("https://intranet.phmtrans.cz/CRM", html, StringComparison.Ordinal);
            Assert.Contains("Provozovatel: Autodoprava Hrbáček s.r.o.", html, StringComparison.Ordinal);
            Assert.Contains("href=\"tel:+420603475612\"", html, StringComparison.Ordinal);
            Assert.Contains("href=\"mailto:info@phmtrans.cz\"", html, StringComparison.Ordinal);
            Assert.Contains("IČO: 26950430", html, StringComparison.Ordinal);
            Assert.Contains("href=\"/img/eu-projekt.png\"", html, StringComparison.Ordinal);
            Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("[telefon]", html, StringComparison.Ordinal);
            Assert.DoesNotContain("[e-mail]", html, StringComparison.Ordinal);
            Assert.DoesNotContain("[adresa]", html, StringComparison.Ordinal);
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

        var login = await client.SendAsync(Request("/login.html", user: false, "phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginHtml = await login.Content.ReadAsStringAsync();
        Assert.Contains("Přihlášení", loginHtml, StringComparison.Ordinal);
        Assert.Contains("Uživatelské jméno", loginHtml, StringComparison.Ordinal);
        Assert.Contains("class=\"brand\" href=\"https://phmtrans.cz/\"", loginHtml, StringComparison.Ordinal);
        Assert.Contains("class=\"site-back\" href=\"https://phmtrans.cz/\">Zpět na web</a>", loginHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Uživatel Pohody", loginHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("[telefon]", loginHtml, StringComparison.Ordinal);

        var www = await client.SendAsync(Request("/vozovy-park?x=1", user: false, "www.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.MovedPermanently, www.StatusCode);
        Assert.Equal("https://phmtrans.cz/vozovy-park?x=1", www.Headers.Location?.OriginalString);

        var wwwHealth = await client.SendAsync(Request("/health", user: false, "WWW.PHMTRANS.CZ"));
        Assert.Equal(HttpStatusCode.OK, wwwHealth.StatusCode);

        foreach (var asset in new[]
        {
            "/landing/logo-dark.png",
            "/landing/logo.png",
            "/landing/truck-white-scania.webp",
            "/landing/truck-white-scania-480.webp",
            "/landing/tanker-scania.webp",
            "/landing/tanker-scania-768.webp",
            "/landing/truck-sunset.webp",
            "/landing/truck-sunset-768.webp",
            "/landing/truck-blue-scania.webp",
            "/landing/fonts/barlow-condensed-800.woff2",
            "/img/eu-projekt.png"
        })
        {
            var file = await client.SendAsync(Request(asset, user: false, "phmtrans.cz"));
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Null(file.Headers.Location);
            var media = file.Content.Headers.ContentType?.MediaType ?? "";
            Assert.DoesNotContain("text/html", media, StringComparison.OrdinalIgnoreCase);
        }

        var poster = await client.SendAsync(Request("/img/eu-projekt.png", user: false, "phmtrans.cz"));
        Assert.Equal("image/png", poster.Content.Headers.ContentType?.MediaType);
        var posterBytes = await poster.Content.ReadAsByteArrayAsync();
        Assert.Equal(3840, BinaryPrimitives.ReadInt32BigEndian(posterBytes.AsSpan(16, 4)));
        Assert.Equal(2160, BinaryPrimitives.ReadInt32BigEndian(posterBytes.AsSpan(20, 4)));

        var wwwLogo = await client.SendAsync(Request("/landing/logo-dark.png", user: false, "www.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.MovedPermanently, wwwLogo.StatusCode);
        Assert.Equal("https://phmtrans.cz/landing/logo-dark.png", wwwLogo.Headers.Location?.OriginalString);

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

        foreach (var (host, target) in new[]
        {
            ("erp.phmtrans.cz", "https://intranet.phmtrans.cz/ERP"),
            ("www.erp.phmtrans.cz", "https://intranet.phmtrans.cz/ERP"),
            ("disp.phmtrans.cz", "https://intranet.phmtrans.cz/Dispecink"),
            ("www.disp.phmtrans.cz", "https://intranet.phmtrans.cz/Dispecink"),
            ("crm.phmtrans.cz", "https://intranet.phmtrans.cz/CRM"),
            ("www.crm.phmtrans.cz", "https://intranet.phmtrans.cz/CRM")
        })
        {
            var home = await client.SendAsync(Request("/", user: false, host));
            Assert.Equal(HttpStatusCode.MovedPermanently, home.StatusCode);
            Assert.Equal(target, home.Headers.Location?.OriginalString);

            var legacyHealth = await client.SendAsync(Request("/health", user: false, host));
            Assert.Equal(HttpStatusCode.OK, legacyHealth.StatusCode);
        }

        var signedErp = await client.SendAsync(Request("/ERP", user: true, "intranet.phmtrans.cz"));
        Assert.Contains("Vydané faktury", await signedErp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var signedDispatch = await client.SendAsync(Request("/Dispecink", user: true, "intranet.phmtrans.cz"));
        Assert.Contains("<h1>Dispečink</h1>", await signedDispatch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var signedCrm = await client.SendAsync(Request("/CRM", user: true, "intranet.phmtrans.cz"));
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

        var identity = TestUsers.Identity(Request, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
