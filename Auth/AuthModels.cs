using Microsoft.AspNetCore.Authentication.Cookies;

namespace PHMTRANS.Auth;

public static class AuthModules
{
    public const string Erp = "ERP";
    public const string Dispatch = "Dispecink";
    public const string Crm = "CRM";

    public static readonly string[] All = [Erp, Dispatch, Crm];

    public static string HomePath(bool allowErp, bool allowDispatch, bool allowCrm)
    {
        if (allowErp)
            return "/ERP";
        if (allowDispatch)
            return "/Dispecink";
        if (allowCrm)
            return "/CRM";
        return "/vyber";
    }
}

public static class AuthClaimTypes
{
    public const string Module = "module";
    public const string ErpReport = "erp_report";
    public const string AdminRole = "Admin";
}

/// <summary>Reporty v ERP. Přístup k nim se nastavuje u uživatele zvlášť.</summary>
public static class ErpReports
{
    public const string Invoices = "faktury";
    public const string Monthly = "mesicni";
    public const string MonthlyGroupB = "mesicni-b";

    public static readonly string[] All = [Invoices, Monthly, MonthlyGroupB];

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [Invoices] = "Vydané faktury",
        [Monthly] = "Vydané faktury – měsíční přehled",
        [MonthlyGroupB] = "Vydané faktury – měsíční přehled skupiny B"
    };

    private static readonly IReadOnlyDictionary<string, string> ApiPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["/api/bi/issued-invoices"] = Invoices,
        ["/api/bi/monthly-invoices"] = Monthly,
        ["/api/bi/monthly-invoices-group-b"] = MonthlyGroupB
    };

    /// <summary>Report, ke kterému patří API cesta, nebo null.</summary>
    public static string? ForApiPath(string path) =>
        ApiPaths.TryGetValue(path.TrimEnd('/'), out var report) ? report : null;

    /// <summary>Uložená hodnota: null znamená všechny reporty (účty založené před zavedením výběru).</summary>
    public static IReadOnlyList<string> Parse(string? stored) =>
        stored is null
            ? All
            : All.Where(report => stored.Split(',', StringSplitOptions.TrimEntries).Contains(report, StringComparer.OrdinalIgnoreCase)).ToArray();

    public static string Store(IEnumerable<string> reports) =>
        string.Join(',', All.Where(report => reports.Contains(report, StringComparer.OrdinalIgnoreCase)));

    public static bool AreKnown(IEnumerable<string> reports) =>
        reports.All(report => All.Contains(report, StringComparer.OrdinalIgnoreCase));
}

public sealed class IntranetOptions
{
    public const string SectionName = "Intranet";

    public string AdminUsername { get; set; } = "";

    public string AdminPassword { get; set; } = "";

    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int LoginPermitPerMinute { get; set; } = 20;
}

public static class AuthStorage
{
    public const string UnavailableMessage =
        "Přihlášení není dostupné. Chybí ConnectionStrings:Dispatch.";

    public const string InvalidCredentialsMessage = "Neplatné přihlašovací údaje.";

    public const string LockedMessage = "Účet je dočasně uzamčený. Zkuste to později.";

    public const string InactiveMessage = "Účet je deaktivovaný.";

    public const string RateLimitMessage = "Příliš mnoho pokusů o přihlášení. Zkuste to později.";

    public const string ShortPasswordMessage = "Heslo musí mít aspoň 8 znaků.";

    public const string LastAdminMessage = "Musí zůstat aspoň jeden aktivní správce.";

    public const string DuplicateUserMessage = "Uživatel s tímto jménem už existuje.";
}

public sealed class AuthUser
{
    public Guid Id { get; set; }

    public string Username { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; }

    public bool AllowErp { get; set; }

    public bool AllowDispatch { get; set; }

    public bool AllowCrm { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public int FailedAttempts { get; set; }

    public DateTimeOffset? LockoutUntil { get; set; }

    /// <summary>Povolené reporty ERP, čárkami. Null = všechny.</summary>
    public string? ErpReports { get; set; }

    public IReadOnlyList<string> AllowedErpReports() =>
        AllowErp ? Auth.ErpReports.Parse(ErpReports) : [];

    public bool CanSeeErpReport(string report) =>
        AllowedErpReports().Contains(report, StringComparer.OrdinalIgnoreCase);

    public string HomePath() => AuthModules.HomePath(AllowErp, AllowDispatch, AllowCrm);
}

public sealed record AuthUserResponse(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsAdmin,
    bool IsActive,
    bool AllowErp,
    bool AllowDispatch,
    bool AllowCrm,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> ErpReports)
{
    public static AuthUserResponse From(AuthUser user) => new(
        user.Id,
        user.Username,
        user.DisplayName,
        user.IsAdmin,
        user.IsActive,
        user.AllowErp,
        user.AllowDispatch,
        user.AllowCrm,
        user.CreatedAt,
        user.LastLoginAt,
        user.AllowedErpReports());
}

public sealed class AuthStoreException(string message, Exception? inner = null) : Exception(message, inner);

public static class IntranetCookies
{
    public const string Name = "phmtrans-auth";

    public static void Apply(CookieAuthenticationOptions options, bool isDevelopment)
    {
        options.Cookie.Name = Name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = isDevelopment
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
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
    }
}
