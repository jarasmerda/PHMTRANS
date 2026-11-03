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
    public const string AdminRole = "Admin";
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
    DateTimeOffset? LastLoginAt)
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
        user.LastLoginAt);
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
