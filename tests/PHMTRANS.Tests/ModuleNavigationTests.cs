using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class ModuleNavigationPageTests
{
    [Fact]
    public void Module_pages_share_a_compact_link_bar_and_keep_logout()
    {
        var root = FindWwwRoot();
        var erp = File.ReadAllText(Path.Combine(root, "erp.html"));
        var dispatch = File.ReadAllText(Path.Combine(root, "dispecink.html"));
        var crm = File.ReadAllText(Path.Combine(root, "crm.html"));

        foreach (var html in new[] { erp, dispatch, crm })
        {
            Assert.Contains("erp.css?v=20", html, StringComparison.Ordinal);
            Assert.Contains("class=\"module-links\"", html, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"Moduly\"", html, StringComparison.Ordinal);
            Assert.Contains("class=\"brand\" href=\"https://phmtrans.cz/\"", html, StringComparison.Ordinal);
            Assert.Contains("class=\"site-back\" href=\"https://phmtrans.cz/\">Zpět na web</a>", html, StringComparison.Ordinal);
            Assert.Contains(">Odhlásit</button>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Rozcestník", html, StringComparison.Ordinal);
            Assert.DoesNotContain("https://www.phmtrans.cz/", html, StringComparison.Ordinal);
            Assert.DoesNotContain("class=\"home\"", html, StringComparison.Ordinal);
        }

            Assert.Contains("aria-current=\"page\">ERP</span>", erp, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"https://intranet.phmtrans.cz/ERP\"", erp, StringComparison.Ordinal);
        Assert.Contains("href=\"https://intranet.phmtrans.cz/Dispecink\">Dispečink</a>", erp, StringComparison.Ordinal);
        Assert.Contains("href=\"https://intranet.phmtrans.cz/CRM\">CRM</a>", erp, StringComparison.Ordinal);
        Assert.Contains("href=\"/heslo\">Heslo</a>", erp, StringComparison.Ordinal);

        Assert.Contains("href=\"https://intranet.phmtrans.cz/ERP\">ERP</a>", dispatch, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Dispečink</span>", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"https://intranet.phmtrans.cz/Dispecink\"", dispatch, StringComparison.Ordinal);
        Assert.Contains("href=\"https://intranet.phmtrans.cz/CRM\">CRM</a>", dispatch, StringComparison.Ordinal);
        Assert.Contains("<h1>Dispečink</h1>", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("Připravujeme", dispatch, StringComparison.Ordinal);

        Assert.Contains("href=\"https://intranet.phmtrans.cz/ERP\">ERP</a>", crm, StringComparison.Ordinal);
        Assert.Contains("href=\"https://intranet.phmtrans.cz/Dispecink\">Dispečink</a>", crm, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">CRM</span>", crm, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"https://intranet.phmtrans.cz/CRM\"", crm, StringComparison.Ordinal);
        Assert.Contains("<h1>CRM</h1>", crm, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Části CRM\"", crm, StringComparison.Ordinal);
        Assert.Contains(">Přehled</button>", crm, StringComparison.Ordinal);
        Assert.Contains(">Zákazníci</button>", crm, StringComparison.Ordinal);
        Assert.Contains(">Příležitosti</button>", crm, StringComparison.Ordinal);
        Assert.Contains(">Aktivity</button>", crm, StringComparison.Ordinal);
        Assert.DoesNotContain("Připravujeme", crm, StringComparison.Ordinal);
    }

    [Fact]
    public void Intranet_pages_link_back_to_the_public_website()
    {
        var root = FindWwwRoot();
        foreach (var name in new[] { "erp.html", "dispecink.html", "crm.html", "admin.html", "heslo.html", "vyber.html", "login.html" })
        {
            var html = File.ReadAllText(Path.Combine(root, name));
            Assert.Contains("erp.css?v=20", html, StringComparison.Ordinal);
            Assert.Contains("<header", html, StringComparison.Ordinal);
            Assert.Contains("class=\"brand\" href=\"https://phmtrans.cz/\"><span>PHM</span> TRANS</a>", html, StringComparison.Ordinal);
            Assert.Contains("class=\"site-back\" href=\"https://phmtrans.cz/\">Zpět na web</a>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("https://www.phmtrans.cz/", html, StringComparison.Ordinal);
        }

        var picker = File.ReadAllText(Path.Combine(root, "vyber.html"));
        Assert.Contains("href=\"/ERP\">ERP</a>", picker, StringComparison.Ordinal);
        Assert.Contains("href=\"/Dispecink\">Dispečink</a>", picker, StringComparison.Ordinal);
        Assert.Contains("href=\"/CRM\">CRM</a>", picker, StringComparison.Ordinal);

        var landing = File.ReadAllText(Path.Combine(root, "landing.html"));
        Assert.DoesNotContain("Zpět na web", landing, StringComparison.Ordinal);
        var jsonStart = landing.IndexOf("<script type=\"application/ld+json\">", StringComparison.Ordinal);
        var jsonEnd = landing.IndexOf("</script>", jsonStart, StringComparison.Ordinal);
        Assert.True(jsonStart >= 0 && jsonEnd > jsonStart);
        var visibleLanding = landing.Remove(jsonStart, jsonEnd + "</script>".Length - jsonStart);
        Assert.DoesNotContain("https://phmtrans.cz/", visibleLanding, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", visibleLanding, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Erp_sections_are_deep_linked_and_bi_keeps_the_invoice_view()
    {
        var root = FindWwwRoot();
        var html = File.ReadAllText(Path.Combine(root, "erp.html"));
        var js = File.ReadAllText(Path.Combine(root, "erp.js"));

        Assert.Contains("<h1>ERP</h1>", html, StringComparison.Ordinal);
        Assert.Contains("erp.js?v=12", html, StringComparison.Ordinal);
        Assert.Contains("multi-filter.js?v=1", html, StringComparison.Ordinal);
        Assert.Contains("id=\"from\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"to\"", html, StringComparison.Ordinal);
        Assert.Contains("<h2 class=\"section-title\">Vydané faktury</h2>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"range\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"customer\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"chart\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"rows\"", html, StringComparison.Ordinal);
        Assert.Contains("/api/bi/issued-invoices", js, StringComparison.Ordinal);
        Assert.Contains("param: \"odberatel\"", js, StringComparison.Ordinal);
    }


    private static string FindWwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var wwwroot = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(wwwroot, "erp.html")))
                return wwwroot;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

public sealed class ModuleNavigationHttpTests : IClassFixture<ModuleNavigationApp>
{
    private readonly ModuleNavigationApp _app;

    public ModuleNavigationHttpTests(ModuleNavigationApp app) => _app = app;

    [Fact]
    public async Task Erp_sections_stay_on_the_protected_invoice_page()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var anonymous = await client.SendAsync(Request("/erp.html", user: false));
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/login.html", anonymous.Headers.Location?.OriginalString, StringComparison.Ordinal);

        var home = await client.SendAsync(Request("/", user: false, host: "erp.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.MovedPermanently, home.StatusCode);
        Assert.Equal("https://intranet.phmtrans.cz/ERP", home.Headers.Location?.OriginalString);

        var signed = await client.SendAsync(Request("/erp.html", user: true));
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var html = await signed.Content.ReadAsStringAsync();
        Assert.Contains("Vydané faktury", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">ERP</span>", html, StringComparison.Ordinal);

        var signedHome = await client.SendAsync(Request("/ERP", user: true, host: "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, signedHome.StatusCode);
        var homeHtml = await signedHome.Content.ReadAsStringAsync();
        Assert.Contains("Vydané faktury", homeHtml, StringComparison.Ordinal);
    }

    private static HttpRequestMessage Request(string path, bool user, string? host = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, path);
        if (host is not null)
            message.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        if (user)
            message.Headers.TryAddWithoutValidation("X-Test-User", "uctarna");
        return message;
    }
}

public sealed class ModuleNavigationApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindContentRoot());
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ModuleNavigationAuthHandler>("Test", _ => { });
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
            if (File.Exists(Path.Combine(dir.FullName, "wwwroot", "erp.html")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

file sealed class ModuleNavigationAuthHandler(
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
