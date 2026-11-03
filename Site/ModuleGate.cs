using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PHMTRANS.Auth;

namespace PHMTRANS.Site;

public static class ModuleGate
{
    public static void UseModuleGate(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var host = SiteHost.Name(context);
            var path = context.Request.Path.Value ?? "/";
            var authenticated = context.User.Identity?.IsAuthenticated == true;

            if (authenticated && IsLoginPage(path) && HttpMethods.IsGet(context.Request.Method))
            {
                var signedIn = await StoredUserAsync(context);
                if (signedIn.Blocked)
                {
                    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    await next();
                    return;
                }

                context.Response.Redirect(signedIn.User?.HomePath() ?? Home(context.User));
                return;
            }

            if (SiteHost.IsIntranet(host) && IsHome(path))
            {
                if (!authenticated)
                {
                    context.Response.Redirect("/login.html");
                    return;
                }

                var signedIn = await StoredUserAsync(context);
                if (signedIn.Blocked)
                {
                    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    context.Response.Redirect("/login.html");
                    return;
                }

                context.Response.Redirect(signedIn.User?.HomePath() ?? Home(context.User));
                return;
            }

            var access = Required(path);
            if (access == RequiredAccess.None)
            {
                await next();
                return;
            }

            if (!authenticated)
            {
                if (IsApi(path))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { message = "Přihlaste se." });
                    return;
                }

                var target = context.Items["IntranetPublicPath"] as string ?? path;
                if (context.Items["IntranetPublicPath"] is null && context.Request.QueryString.HasValue)
                    target += context.Request.QueryString.Value;
                context.Response.Redirect("/login.html?returnUrl=" + Uri.EscapeDataString(target));
                return;
            }

            var stored = await StoredUserAsync(context);
            if (stored.Blocked)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (IsApi(path))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { message = AuthStorage.InactiveMessage });
                    return;
                }

                context.Response.Redirect("/login.html");
                return;
            }

            if (!Allows(context.User, stored.User, access))
            {
                if (IsApi(path))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { message = DeniedMessage(access) });
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(DeniedPage(access), Encoding.UTF8);
                return;
            }

            await next();
        });
    }

    private static bool IsHome(string path) =>
        path is "" or "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoginPage(string path) =>
        path.Equals("/login.html", StringComparison.OrdinalIgnoreCase);

    private static bool IsApi(string path) =>
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    private static string Home(ClaimsPrincipal principal)
    {
        if (principal.HasClaim(AuthClaimTypes.Module, AuthModules.Erp))
            return "/ERP";
        if (principal.HasClaim(AuthClaimTypes.Module, AuthModules.Dispatch))
            return "/Dispecink";
        if (principal.HasClaim(AuthClaimTypes.Module, AuthModules.Crm))
            return "/CRM";
        return "/vyber";
    }

    private static RequiredAccess Required(string path)
    {
        if (path.Equals("/erp.html", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/bi", StringComparison.OrdinalIgnoreCase))
            return RequiredAccess.Erp;
        if (path.Equals("/dispecink.html", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/dispatch", StringComparison.OrdinalIgnoreCase))
            return RequiredAccess.Dispatch;
        if (path.Equals("/crm.html", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/crm", StringComparison.OrdinalIgnoreCase))
            return RequiredAccess.Crm;
        if (path.Equals("/admin.html", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
            return RequiredAccess.Admin;
        if (path.Equals("/heslo.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/vyber.html", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
            return RequiredAccess.User;
        return RequiredAccess.None;
    }

    private static bool Allows(ClaimsPrincipal principal, AuthUser? user, RequiredAccess access)
    {
        if (user is not null)
        {
            return access switch
            {
                RequiredAccess.Erp => user.AllowErp,
                RequiredAccess.Dispatch => user.AllowDispatch,
                RequiredAccess.Crm => user.AllowCrm,
                RequiredAccess.Admin => user.IsAdmin,
                RequiredAccess.User => user.IsActive,
                _ => true
            };
        }

        return access switch
        {
            RequiredAccess.Erp => principal.HasClaim(AuthClaimTypes.Module, AuthModules.Erp),
            RequiredAccess.Dispatch => principal.HasClaim(AuthClaimTypes.Module, AuthModules.Dispatch),
            RequiredAccess.Crm => principal.HasClaim(AuthClaimTypes.Module, AuthModules.Crm),
            RequiredAccess.Admin => principal.IsInRole(AuthClaimTypes.AdminRole),
            RequiredAccess.User => principal.Identity?.IsAuthenticated == true,
            _ => true
        };
    }

    private static async Task<StoredUser> StoredUserAsync(HttpContext context)
    {
        var idValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var id))
            return new StoredUser(null, false);

        var store = context.RequestServices.GetRequiredService<IAuthStore>();
        if (!store.IsAvailable)
            return new StoredUser(null, false);

        try
        {
            var user = await store.FindByIdAsync(id, context.RequestAborted);
            if (user is null || !user.IsActive)
                return new StoredUser(user, true);
            return new StoredUser(user, false);
        }
        catch (AuthStoreException)
        {
            return new StoredUser(null, false);
        }
    }

    private static string DeniedMessage(RequiredAccess access) =>
        access == RequiredAccess.Admin
            ? "Správu uživatelů smí jen správce."
            : "Na tento modul nemáte oprávnění.";

    private static string DeniedPage(RequiredAccess access)
    {
        var message = DeniedMessage(access);
        return $$"""
            <!DOCTYPE html>
            <html lang="cs">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Bez oprávnění – PHM Trans</title>
              <link rel="stylesheet" href="/erp.css?v=11">
            </head>
            <body>
              <header class="login-bar">
                <a class="brand" href="https://phmtrans.cz/"><span>PHM</span> TRANS</a>
                <div class="header-actions">
                  <a class="site-back" href="https://phmtrans.cz/">Zpět na web</a>
                </div>
              </header>
              <main class="login-card">
                <h1>Bez oprávnění</h1>
                <p>{{message}}</p>
                <p><a href="/vyber">Výběr modulu</a> · <a href="/heslo">Heslo</a></p>
              </main>
            </body>
            </html>
            """;
    }

    private readonly record struct StoredUser(AuthUser? User, bool Blocked);

    private enum RequiredAccess
    {
        None,
        User,
        Erp,
        Dispatch,
        Crm,
        Admin
    }
}
