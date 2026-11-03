using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PHMTRANS.Auth;

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/api/login", (Delegate)Login);
        app.MapPost("/api/logout", (Delegate)Logout);
        app.MapGet("/api/auth/me", (Delegate)Me);
        app.MapPost("/api/auth/password", (Delegate)ChangePassword);
        app.MapGet("/api/admin/users", (Delegate)ListUsers);
        app.MapPost("/api/admin/users", (Delegate)CreateUser);
        app.MapPut("/api/admin/users/{id:guid}", (Delegate)UpdateUser);
        app.MapPost("/api/admin/users/{id:guid}/password", (Delegate)ResetPassword);
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        IAuthStore store,
        IPasswordHasher<AuthUser> hasher,
        LoginRateLimiter limiter,
        IOptions<IntranetOptions> options,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!limiter.TryAcquire(ClientKey(context), settings.LoginPermitPerMinute, DateTimeOffset.UtcNow))
            return Results.Json(new { message = AuthStorage.RateLimitMessage }, statusCode: StatusCodes.Status429TooManyRequests);

        if (!store.IsAvailable)
            return Results.Json(new { message = AuthStorage.UnavailableMessage }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var username = request.Username?.Trim() ?? "";
        var password = request.Password ?? "";
        if (username.Length == 0 || password.Length == 0)
            return Results.Json(new { message = AuthStorage.InvalidCredentialsMessage }, statusCode: StatusCodes.Status401Unauthorized);

        AuthUser? user;
        try
        {
            user = await store.FindByUsernameAsync(username, cancellationToken);
        }
        catch (AuthStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (user is null)
            return Results.Json(new { message = AuthStorage.InvalidCredentialsMessage }, statusCode: StatusCodes.Status401Unauthorized);

        if (user.LockoutUntil is { } until && until > DateTimeOffset.UtcNow)
            return Results.Json(new { message = AuthStorage.LockedMessage }, statusCode: StatusCodes.Status429TooManyRequests);

        var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verify == PasswordVerificationResult.Failed)
            return await RejectPasswordAsync(store, user, settings, cancellationToken);

        if (!user.IsActive)
            return Results.Json(new { message = AuthStorage.InactiveMessage }, statusCode: StatusCodes.Status401Unauthorized);

        try
        {
            if (verify == PasswordVerificationResult.SuccessRehashNeeded)
                await store.SetPasswordHashAsync(user.Id, hasher.HashPassword(user, password), cancellationToken);

            await store.RecordLoginAsync(user.Id, cancellationToken);
        }
        catch (AuthStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(user));
        return Results.Ok(new { username = user.Username, displayName = user.DisplayName, redirect = user.HomePath() });
    }

    private static async Task<IResult> Logout(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok();
    }

    private static async Task<IResult> Me(HttpContext context, IAuthStore store, CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync(context, store, cancellationToken);
        if (user is null)
            return Results.Json(new { message = "Přihlaste se." }, statusCode: StatusCodes.Status401Unauthorized);

        return Results.Ok(AuthUserResponse.From(user));
    }

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request,
        HttpContext context,
        IAuthStore store,
        IPasswordHasher<AuthUser> hasher,
        CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync(context, store, cancellationToken);
        if (user is null)
            return Results.Json(new { message = "Přihlaste se." }, statusCode: StatusCodes.Status401Unauthorized);

        var current = request.CurrentPassword ?? "";
        var next = request.NewPassword ?? "";
        if (next.Length < AuthBootstrap.MinimumPasswordLength)
            return Results.BadRequest(new { message = AuthStorage.ShortPasswordMessage });

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, current) == PasswordVerificationResult.Failed)
            return Results.Json(new { message = "Současné heslo nesedí." }, statusCode: StatusCodes.Status400BadRequest);

        try
        {
            await store.SetPasswordHashAsync(user.Id, hasher.HashPassword(user, next), cancellationToken);
        }
        catch (AuthStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new { message = "Heslo je změněné." });
    }

    private static async Task<IResult> ListUsers(IAuthStore store, CancellationToken cancellationToken)
    {
        try
        {
            var users = await store.ListAsync(cancellationToken);
            return Results.Ok(users.Select(AuthUserResponse.From).ToArray());
        }
        catch (AuthStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> CreateUser(
        CreateAuthUserRequest request,
        IAuthStore store,
        IPasswordHasher<AuthUser> hasher,
        CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim() ?? "";
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim();
        var password = request.Password ?? "";
        if (username.Length == 0 || username.Length > 80 || displayName.Length > 160)
            return Results.BadRequest(new { message = "Vyplňte uživatelské jméno." });
        if (password.Length < AuthBootstrap.MinimumPasswordLength)
            return Results.BadRequest(new { message = AuthStorage.ShortPasswordMessage });

        var user = new AuthUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = displayName,
            IsAdmin = request.IsAdmin,
            IsActive = request.IsActive,
            AllowErp = request.AllowErp,
            AllowDispatch = request.AllowDispatch,
            AllowCrm = request.AllowCrm,
            CreatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, password);

        try
        {
            if (await store.FindByUsernameAsync(username, cancellationToken) is not null)
                return Results.BadRequest(new { message = AuthStorage.DuplicateUserMessage });

            await store.CreateAsync(user, cancellationToken);
        }
        catch (AuthStoreException exception) when (exception.Message == AuthStorage.DuplicateUserMessage)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
        catch (AuthStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(AuthUserResponse.From(user));
    }

    private static async Task<IResult> UpdateUser(
        Guid id,
        UpdateAuthUserRequest request,
        IAuthStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await store.FindByIdAsync(id, cancellationToken);
            if (user is null)
                return Results.NotFound(new { message = "Uživatel neexistuje." });

            var displayName = request.DisplayName?.Trim() ?? "";
            if (displayName.Length == 0 || displayName.Length > 160)
                return Results.BadRequest(new { message = "Vyplňte zobrazované jméno." });

            var removesActiveAdmin = user.IsAdmin && user.IsActive && (!request.IsAdmin || !request.IsActive);
            if (removesActiveAdmin && await store.CountActiveAdminsAsync(cancellationToken) <= 1)
                return Results.BadRequest(new { message = AuthStorage.LastAdminMessage });

            user.DisplayName = displayName;
            user.IsAdmin = request.IsAdmin;
            user.IsActive = request.IsActive;
            user.AllowErp = request.AllowErp;
            user.AllowDispatch = request.AllowDispatch;
            user.AllowCrm = request.AllowCrm;
            await store.UpdateAsync(user, cancellationToken);
            return Results.Ok(AuthUserResponse.From(user));
        }
        catch (AuthStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ResetPassword(
        Guid id,
        ResetPasswordRequest request,
        IAuthStore store,
        IPasswordHasher<AuthUser> hasher,
        CancellationToken cancellationToken)
    {
        var password = request.Password ?? "";
        if (password.Length < AuthBootstrap.MinimumPasswordLength)
            return Results.BadRequest(new { message = AuthStorage.ShortPasswordMessage });

        try
        {
            var user = await store.FindByIdAsync(id, cancellationToken);
            if (user is null)
                return Results.NotFound(new { message = "Uživatel neexistuje." });

            await store.SetPasswordHashAsync(id, hasher.HashPassword(user, password), cancellationToken);
        }
        catch (AuthStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new { message = "Heslo je nastavené." });
    }

    private static async Task<IResult> RejectPasswordAsync(
        IAuthStore store,
        AuthUser user,
        IntranetOptions settings,
        CancellationToken cancellationToken)
    {
        var attempts = user.FailedAttempts + 1;
        DateTimeOffset? lockout = null;
        if (attempts >= Math.Max(1, settings.MaxFailedAttempts))
        {
            attempts = 0;
            lockout = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, settings.LockoutMinutes));
        }

        try
        {
            await store.SetFailuresAsync(user.Id, attempts, lockout, cancellationToken);
        }
        catch (AuthStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (lockout is not null)
            return Results.Json(new { message = AuthStorage.LockedMessage }, statusCode: StatusCodes.Status429TooManyRequests);

        return Results.Json(new { message = AuthStorage.InvalidCredentialsMessage }, statusCode: StatusCodes.Status401Unauthorized);
    }

    internal static ClaimsPrincipal Principal(AuthUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("display_name", user.DisplayName)
        };
        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, AuthClaimTypes.AdminRole));
        if (user.AllowErp)
            claims.Add(new Claim(AuthClaimTypes.Module, AuthModules.Erp));
        if (user.AllowDispatch)
            claims.Add(new Claim(AuthClaimTypes.Module, AuthModules.Dispatch));
        if (user.AllowCrm)
            claims.Add(new Claim(AuthClaimTypes.Module, AuthModules.Crm));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static async Task<AuthUser?> CurrentUserAsync(HttpContext context, IAuthStore store, CancellationToken cancellationToken)
    {
        var idValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (store.IsAvailable && Guid.TryParse(idValue, out var id))
        {
            try
            {
                return await store.FindByIdAsync(id, cancellationToken);
            }
            catch (AuthStoreException)
            {
                return null;
            }
        }

        var name = context.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(name) || !store.IsAvailable)
            return null;

        try
        {
            return await store.FindByUsernameAsync(name, cancellationToken);
        }
        catch (AuthStoreException)
        {
            return null;
        }
    }

    internal static string ClientKey(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(forwarded))
            return forwarded;

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public sealed record LoginRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CreateAuthUserRequest(
    string? Username,
    string? DisplayName,
    string? Password,
    bool IsAdmin,
    bool IsActive,
    bool AllowErp,
    bool AllowDispatch,
    bool AllowCrm);

public sealed record UpdateAuthUserRequest(
    string? DisplayName,
    bool IsAdmin,
    bool IsActive,
    bool AllowErp,
    bool AllowDispatch,
    bool AllowCrm);

public sealed record ResetPasswordRequest(string? Password);
