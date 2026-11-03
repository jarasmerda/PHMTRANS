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
            Assert.Contains("erp.css?v=6", html, StringComparison.Ordinal);
            Assert.Contains("class=\"module-links\"", html, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"Moduly\"", html, StringComparison.Ordinal);
            Assert.Contains(">Odhlásit</button>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Rozcestník", html, StringComparison.Ordinal);
            Assert.DoesNotContain("https://www.phmtrans.cz/", html, StringComparison.Ordinal);
            Assert.DoesNotContain("class=\"home\"", html, StringComparison.Ordinal);
        }

        Assert.Contains("aria-current=\"page\">ERP</span>", erp, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"https://erp.phmtrans.cz/\"", erp, StringComparison.Ordinal);
        Assert.Contains("href=\"https://disp.phmtrans.cz/\">Dispečink</a>", erp, StringComparison.Ordinal);
        Assert.Contains("href=\"https://crm.phmtrans.cz/\">CRM</a>", erp, StringComparison.Ordinal);

        Assert.Contains("href=\"https://erp.phmtrans.cz/\">ERP</a>", dispatch, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Dispečink</span>", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"https://disp.phmtrans.cz/\"", dispatch, StringComparison.Ordinal);
        Assert.Contains("href=\"https://crm.phmtrans.cz/\">CRM</a>", dispatch, StringComparison.Ordinal);
        Assert.Contains("<h1>Dispečink</h1>", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("erp-sections", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("Připravujeme", dispatch, StringComparison.Ordinal);

        Assert.Contains("href=\"https://erp.phmtrans.cz/\">ERP</a>", crm, StringComparison.Ordinal);
        Assert.Contains("href=\"https://disp.phmtrans.cz/\">Dispečink</a>", crm, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">CRM</span>", crm, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"https://crm.phmtrans.cz/\"", crm, StringComparison.Ordinal);
        Assert.Contains("<h1>CRM</h1>", crm, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Části CRM\"", crm, StringComparison.Ordinal);
        Assert.Contains(">Přehled</button>", crm, StringComparison.Ordinal);
        Assert.Contains(">Zákazníci</button>", crm, StringComparison.Ordinal);
        Assert.Contains(">Příležitosti</button>", crm, StringComparison.Ordinal);
        Assert.Contains(">Aktivity</button>", crm, StringComparison.Ordinal);
        Assert.DoesNotContain("erp-sections", crm, StringComparison.Ordinal);
        Assert.DoesNotContain("Připravujeme", crm, StringComparison.Ordinal);
    }

    [Fact]
    public void Erp_sections_are_deep_linked_and_bi_keeps_the_invoice_view()
    {
        var root = FindWwwRoot();
        var html = File.ReadAllText(Path.Combine(root, "erp.html"));
        var js = File.ReadAllText(Path.Combine(root, "erp.js"));

        Assert.Contains("<h1>ERP</h1>", html, StringComparison.Ordinal);
        Assert.Contains("erp.js?v=5", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Části ERP\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#bi\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#zpracovani-objednavek\">Zpracování objednávek</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#vytvareni-objednavek\">Vytváření objednávek</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#uctovani-faktur\">Účtování faktur</a>", html, StringComparison.Ordinal);
        Assert.Contains("data-erp-section=\"bi\"", html, StringComparison.Ordinal);
        Assert.Contains("<h2 class=\"section-title\">Vydané faktury</h2>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"range\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"customer\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"chart\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"rows\"", html, StringComparison.Ordinal);
        Assert.Contains("data-erp-section=\"zpracovani-objednavek\"", html, StringComparison.Ordinal);
        Assert.Contains("data-erp-section=\"vytvareni-objednavek\"", html, StringComparison.Ordinal);
        Assert.Contains("data-erp-section=\"uctovani-faktur\"", html, StringComparison.Ordinal);
        Assert.Equal(3, Count(html, "Připravujeme"));
        Assert.Contains("hashchange", js, StringComparison.Ordinal);
        Assert.Contains("zpracovani-objednavek", js, StringComparison.Ordinal);
        Assert.Contains("vytvareni-objednavek", js, StringComparison.Ordinal);
        Assert.Contains("uctovani-faktur", js, StringComparison.Ordinal);
        Assert.Contains("/api/bi/issued-invoices", js, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
        Assert.Equal("/login.html", anonymous.Headers.Location?.OriginalString);

        var home = await client.SendAsync(Request("/", user: false, host: "erp.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/login.html", home.Headers.Location?.OriginalString);

        var signed = await client.SendAsync(Request("/erp.html", user: true));
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var html = await signed.Content.ReadAsStringAsync();
        Assert.Contains("href=\"#uctovani-faktur\"", html, StringComparison.Ordinal);
        Assert.Contains("Připravujeme", html, StringComparison.Ordinal);
        Assert.Contains("Vydané faktury", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">ERP</span>", html, StringComparison.Ordinal);

        var signedHome = await client.SendAsync(Request("/", user: true, host: "erp.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, signedHome.StatusCode);
        Assert.Contains("Části ERP", await signedHome.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, Request.Headers["X-Test-User"].ToString())],
            "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}
