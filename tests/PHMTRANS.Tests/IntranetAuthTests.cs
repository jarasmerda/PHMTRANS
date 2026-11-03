using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHMTRANS.Auth;
using PHMTRANS.Crm;
using PHMTRANS.Dispatch;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class AuthBootstrapTests
{
    private const string Password = "Heslo-tajne-123";

    [Fact]
    public async Task Bootstrap_creates_one_admin_and_never_logs_or_resets_the_password()
    {
        var store = new MemoryAuthStore();
        var hasher = new PasswordHasher<AuthUser>();
        var logger = new ListLogger();
        var options = new IntranetOptions { AdminUsername = "spravce", AdminPassword = Password };

        await AuthBootstrap.EnsureAdminAsync(store, options, hasher, logger, CancellationToken.None);
        await AuthBootstrap.EnsureAdminAsync(store, new IntranetOptions { AdminUsername = "jiny", AdminPassword = "Jine-heslo-999" }, hasher, logger, CancellationToken.None);

        var users = await store.ListAsync(CancellationToken.None);
        var admin = Assert.Single(users);
        Assert.Equal("spravce", admin.Username);
        Assert.True(admin.IsAdmin);
        Assert.True(admin.IsActive);
        Assert.True(admin.AllowErp);
        Assert.True(admin.AllowDispatch);
        Assert.True(admin.AllowCrm);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(admin, admin.PasswordHash, Password));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(admin, admin.PasswordHash, "Jine-heslo-999"));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("Jine-heslo-999", StringComparison.Ordinal));
        Assert.Contains("dbo.Auth_User", AuthSchema.Script, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID", AuthSchema.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", AuthSchema.Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bootstrap_skips_when_password_is_missing_or_short_or_an_admin_exists()
    {
        var hasher = new PasswordHasher<AuthUser>();
        var logger = new ListLogger();
        var empty = new MemoryAuthStore();
        await AuthBootstrap.EnsureAdminAsync(empty, new IntranetOptions { AdminUsername = "spravce", AdminPassword = "" }, hasher, logger, CancellationToken.None);
        await AuthBootstrap.EnsureAdminAsync(empty, new IntranetOptions { AdminUsername = "spravce", AdminPassword = "kratke" }, hasher, logger, CancellationToken.None);
        Assert.Empty(await empty.ListAsync(CancellationToken.None));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("kratke", StringComparison.Ordinal));

        var existing = new MemoryAuthStore();
        await existing.CreateAsync(new AuthUser
        {
            Id = Guid.NewGuid(),
            Username = "puvodni",
            DisplayName = "Původní",
            PasswordHash = hasher.HashPassword(new AuthUser(), "Puvodni-heslo-1"),
            IsAdmin = true,
            IsActive = false,
            CreatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await AuthBootstrap.EnsureAdminAsync(existing, new IntranetOptions { AdminUsername = "novy", AdminPassword = Password }, hasher, logger, CancellationToken.None);
        Assert.Equal("puvodni", Assert.Single(await existing.ListAsync(CancellationToken.None)).Username);
    }

    [Fact]
    public void Production_cookie_is_secure_httponly_and_lax()
    {
        var options = new CookieAuthenticationOptions();
        IntranetCookies.Apply(options, isDevelopment: false);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal("phmtrans-auth", options.Cookie.Name);

        var development = new CookieAuthenticationOptions();
        IntranetCookies.Apply(development, isDevelopment: true);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, development.Cookie.SecurePolicy);
    }
}

public sealed class IntranetAuthHttpTests : IClassFixture<IntranetAuthApp>
{
    private readonly IntranetAuthApp _app;

    public IntranetAuthHttpTests(IntranetAuthApp app) => _app = app;

    [Fact]
    public async Task Bootstrap_admin_can_sign_in_and_open_modules()
    {
        using var client = Client();
        var login = await Login(client, "spravce", "Heslo-spravce-123", "198.51.100.10");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookie = string.Join(";", login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("phmtrans-auth=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        var body = await login.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Heslo-spravce-123", body, StringComparison.Ordinal);
        using (var json = JsonDocument.Parse(body))
            Assert.Equal("/ERP", json.RootElement.GetProperty("redirect").GetString());

        var erp = await client.SendAsync(Get("/erp", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, erp.StatusCode);
        Assert.Contains("href=\"#uctovani-faktur\"", await erp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var home = await client.SendAsync(Get("/", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/ERP", home.Headers.Location?.OriginalString);

        var admin = await client.SendAsync(Get("/Admin", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Contains("Správa uživatelů", await admin.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var css = await client.SendAsync(Get("/ERP/erp.css?v=17", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Contains("css", css.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);

        var logout = await client.PostAsync("/api/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var again = await client.SendAsync(Get("/ERP", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.StartsWith("/login.html", again.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_permissions_block_pages_and_apis()
    {
        using var admin = Client();
        Assert.Equal(HttpStatusCode.OK, (await Login(admin, "spravce", "Heslo-spravce-123", "198.51.100.20")).StatusCode);
        var created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = "obchod",
            displayName = "Obchod",
            password = "Heslo-obchod-1",
            isAdmin = false,
            isActive = true,
            allowErp = false,
            allowDispatch = false,
            allowCrm = true
        });
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.DoesNotContain("Heslo-obchod-1", createdBody, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", createdBody, StringComparison.OrdinalIgnoreCase);

        using var user = Client();
        Assert.Equal(HttpStatusCode.OK, (await Login(user, "obchod", "Heslo-obchod-1", "198.51.100.21")).StatusCode);

        var erp = await user.SendAsync(Get("/ERP", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Forbidden, erp.StatusCode);
        var denied = await erp.Content.ReadAsStringAsync();
        Assert.Contains("Na tento modul nemáte oprávnění.", denied, StringComparison.Ordinal);
        Assert.Contains("class=\"brand\" href=\"https://phmtrans.cz/\"", denied, StringComparison.Ordinal);
        Assert.Contains("class=\"site-back\" href=\"https://phmtrans.cz/\">Zpět na web</a>", denied, StringComparison.Ordinal);

        var invoices = await user.SendAsync(Get("/ERP/api/bi/issued-invoices", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Forbidden, invoices.StatusCode);

        var dispatch = await user.SendAsync(Get("/api/dispatch/board", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Forbidden, dispatch.StatusCode);

        var crm = await user.SendAsync(Get("/crm", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, crm.StatusCode);
        Assert.Contains("<h1>CRM</h1>", await crm.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var board = await user.SendAsync(Get("/CRM/api/crm/board", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);

        var adminPage = await user.SendAsync(Get("/admin", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Forbidden, adminPage.StatusCode);
        Assert.Contains("Správu uživatelů smí jen správce.", await adminPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_resets_password_modules_and_deactivation()
    {
        using var admin = Client();
        Assert.Equal(HttpStatusCode.OK, (await Login(admin, "spravce", "Heslo-spravce-123", "198.51.100.30")).StatusCode);
        var created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = "dispecer",
            displayName = "Dispečer",
            password = "Heslo-disp-123",
            isAdmin = false,
            isActive = true,
            allowErp = false,
            allowDispatch = true,
            allowCrm = false
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("id").GetGuid();

        var updated = await admin.PutAsJsonAsync($"/api/admin/users/{id}", new
        {
            displayName = "Dispečer Nový",
            isAdmin = false,
            isActive = true,
            allowErp = true,
            allowDispatch = true,
            allowCrm = false
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var reset = await admin.PostAsJsonAsync($"/api/admin/users/{id}/password", new { password = "Heslo-nove-123" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.DoesNotContain("Heslo-nove-123", await reset.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var user = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(user, "dispecer", "Heslo-disp-123", "198.51.100.31")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(user, "dispecer", "Heslo-nove-123", "198.51.100.31")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.SendAsync(Get("/ERP", "intranet.phmtrans.cz"))).StatusCode);

        var changed = await user.PostAsJsonAsync("/api/auth/password", new { currentPassword = "Heslo-nove-123", newPassword = "Heslo-vlastni-1" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var again = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(again, "dispecer", "Heslo-nove-123", "198.51.100.32")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(again, "dispecer", "Heslo-vlastni-1", "198.51.100.32")).StatusCode);

        var off = await admin.PutAsJsonAsync($"/api/admin/users/{id}", new
        {
            displayName = "Dispečer Nový",
            isAdmin = false,
            isActive = false,
            allowErp = true,
            allowDispatch = true,
            allowCrm = false
        });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var blocked = await again.SendAsync(Get("/api/dispatch/board", "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        Assert.Contains(AuthStorage.InactiveMessage, await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var retired = Client();
        var denied = await Login(retired, "dispecer", "Heslo-vlastni-1", "198.51.100.33");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains(AuthStorage.InactiveMessage, await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var lastAdmin = await admin.PutAsJsonAsync($"/api/admin/users/{(await AdminId(admin))}", new
        {
            displayName = "spravce",
            isAdmin = false,
            isActive = true,
            allowErp = true,
            allowDispatch = true,
            allowCrm = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, lastAdmin.StatusCode);
        Assert.Contains(AuthStorage.LastAdminMessage, await lastAdmin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account()
    {
        using var admin = Client();
        Assert.Equal(HttpStatusCode.OK, (await Login(admin, "spravce", "Heslo-spravce-123", "198.51.100.40")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = "zamceny",
            displayName = "Zamčený",
            password = "Heslo-zamcene-1",
            isAdmin = false,
            isActive = true,
            allowErp = true,
            allowDispatch = false,
            allowCrm = false
        })).StatusCode);

        using var user = Client();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var failed = await Login(user, "zamceny", "spatne-heslo", "198.51.100.41");
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var locked = await Login(user, "zamceny", "spatne-heslo", "198.51.100.41");
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Contains(AuthStorage.LockedMessage, await locked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var still = await Login(user, "zamceny", "Heslo-zamcene-1", "198.51.100.41");
        Assert.Equal(HttpStatusCode.TooManyRequests, still.StatusCode);
    }

    private HttpClient Client() =>
        _app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<Guid> AdminId(HttpClient admin)
    {
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        return users.EnumerateArray().Single(item => item.GetProperty("username").GetString() == "spravce").GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string username, string password, string ip)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/login")
        {
            Content = JsonContent.Create(new { username, password })
        };
        message.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return await client.SendAsync(message);
    }

    private static HttpRequestMessage Get(string path, string host)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        return message;
    }
}

public sealed class IntranetRateLimitTests : IClassFixture<IntranetRateLimitApp>
{
    private readonly IntranetRateLimitApp _app;

    public IntranetRateLimitTests(IntranetRateLimitApp app) => _app = app;

    [Fact]
    public async Task Login_is_rate_limited_per_address()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "/api/login")
            {
                Content = JsonContent.Create(new { username = "spravce", password = "Heslo-spravce-123" })
            };
            message.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.50");
            last = await client.SendAsync(message);
        }

        Assert.NotNull(last);
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.Contains(AuthStorage.RateLimitMessage, await last.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

public sealed class IntranetWithoutDatabaseTests : IClassFixture<IntranetBareApp>
{
    private readonly IntranetBareApp _app;

    public IntranetWithoutDatabaseTests(IntranetBareApp app) => _app = app;

    [Fact]
    public async Task App_starts_and_login_explains_the_missing_connection_string()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var legacyHealth = new HttpRequestMessage(HttpMethod.Get, "/health");
        legacyHealth.Headers.TryAddWithoutValidation("X-Forwarded-Host", "erp.phmtrans.cz");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(legacyHealth)).StatusCode);

        var login = await client.PostAsJsonAsync("/api/login", new { username = "kdokoli", password = "Heslo-ktere-neni-v-logu-1" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        var body = await login.Content.ReadAsStringAsync();
        Assert.Contains("ConnectionStrings:Dispatch", body, StringComparison.Ordinal);
        Assert.Contains("Přihlášení není dostupné", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Heslo-ktere-neni-v-logu-1", body, StringComparison.Ordinal);
    }
}

public sealed class IntranetAuthApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(ContentRoot());
        builder.UseSetting("ConnectionStrings:Dispatch", "");
        builder.UseSetting("Intranet:AdminUsername", "spravce");
        builder.UseSetting("Intranet:AdminPassword", "Heslo-spravce-123");
        builder.UseSetting("Intranet:LoginPermitPerMinute", "1000");
        builder.UseSetting("Intranet:MaxFailedAttempts", "5");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthStore>();
            services.AddSingleton<IAuthStore, MemoryAuthStore>();
            services.RemoveAll<ICrmStore>();
            services.AddSingleton<ICrmStore, MemoryCrmStore>();
            services.RemoveAll<IDispatchStore>();
            services.AddSingleton<IDispatchStore, MemoryDispatchStore>();
        });
    }

    internal static string ContentRoot()
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

public sealed class IntranetRateLimitApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(IntranetAuthApp.ContentRoot());
        builder.UseSetting("ConnectionStrings:Dispatch", "");
        builder.UseSetting("Intranet:AdminUsername", "spravce");
        builder.UseSetting("Intranet:AdminPassword", "Heslo-spravce-123");
        builder.UseSetting("Intranet:LoginPermitPerMinute", "2");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthStore>();
            services.AddSingleton<IAuthStore, MemoryAuthStore>();
        });
    }
}

public sealed class IntranetBareApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(IntranetAuthApp.ContentRoot());
        builder.UseSetting("ConnectionStrings:Dispatch", "");
        builder.UseSetting("Intranet:AdminUsername", "");
        builder.UseSetting("Intranet:AdminPassword", "");
    }
}

file sealed class ListLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

public sealed class IntranetRoutingTests : IClassFixture<PublicSiteApp>
{
    private readonly PublicSiteApp _app;

    public IntranetRoutingTests(PublicSiteApp app) => _app = app;

    [Fact]
    public async Task Prefixes_are_case_insensitive_and_legacy_hosts_keep_the_path()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var legacy = await client.SendAsync(Get("/api/bi/issued-invoices?from=2026-01-01", "erp.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.MovedPermanently, legacy.StatusCode);
        Assert.Equal("https://intranet.phmtrans.cz/ERP/api/bi/issued-invoices?from=2026-01-01", legacy.Headers.Location?.OriginalString);

        var page = await client.SendAsync(Get("/erp.html", "disp.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.MovedPermanently, page.StatusCode);
        Assert.Equal("https://intranet.phmtrans.cz/Dispecink/erp.html", page.Headers.Location?.OriginalString);

        var root = await client.SendAsync(Get("/", "CRM.PHMTRANS.CZ", user: false));
        Assert.Equal(HttpStatusCode.MovedPermanently, root.StatusCode);
        Assert.Equal("https://intranet.phmtrans.cz/CRM", root.Headers.Location?.OriginalString);

        var lower = await client.SendAsync(Get("/erp", "intranet.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Contains("href=\"#uctovani-faktur\"", await lower.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var login = await client.SendAsync(Get("/ERP", "intranet.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("returnUrl=%2FERP", login.Headers.Location?.OriginalString, StringComparison.OrdinalIgnoreCase);

        var asset = await client.SendAsync(Get("/Dispecink/dispecink.js?v=5", "intranet.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("javascript", asset.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);

        var anonymousApi = await client.SendAsync(Get("/CRM/api/crm/board", "intranet.phmtrans.cz", user: false));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousApi.StatusCode);

        var prefixedApi = await client.SendAsync(Get("/Dispecink/api/dispatch/board", "www.intranet.phmtrans.cz", user: true));
        Assert.Equal(HttpStatusCode.InternalServerError, prefixedApi.StatusCode);
        Assert.Contains("ConnectionStrings:Dispatch", await prefixedApi.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        foreach (var host in new[] { "phmtrans.cz", "www.phmtrans.cz", "intranet.phmtrans.cz", "erp.phmtrans.cz" })
        {
            var health = await client.SendAsync(Get("/health", host, user: false));
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    private static HttpRequestMessage Get(string path, string host, bool user)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        if (user)
            message.Headers.TryAddWithoutValidation("X-Test-User", "uctarna");
        return message;
    }
}
