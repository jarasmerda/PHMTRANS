using Microsoft.AspNetCore.Identity;

namespace PHMTRANS.Auth;

public static class AuthBootstrap
{
    public const int MinimumPasswordLength = 8;

    public static async Task EnsureAdminAsync(
        IAuthStore store,
        IntranetOptions options,
        IPasswordHasher<AuthUser> hasher,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!store.IsAvailable)
            return;

        if (await store.AnyAdminAsync(cancellationToken))
            return;

        var username = options.AdminUsername?.Trim() ?? "";
        var password = options.AdminPassword ?? "";
        if (username.Length == 0 || password.Length == 0)
        {
            logger.LogWarning(
                "První správce intranetu se nezaložil. Nastavte Intranet:AdminUsername a Intranet:AdminPassword (proměnné Intranet__AdminUsername a Intranet__AdminPassword). Heslo se do logu nezapisuje.");
            return;
        }

        if (password.Length < MinimumPasswordLength)
        {
            logger.LogWarning(
                "První správce intranetu se nezaložil. Heslo v Intranet:AdminPassword je kratší než {Minimum} znaků. Heslo se do logu nezapisuje.",
                MinimumPasswordLength);
            return;
        }

        var user = new AuthUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = username,
            PasswordHash = hasher.HashPassword(new AuthUser { Username = username }, password),
            IsAdmin = true,
            IsActive = true,
            AllowErp = true,
            AllowDispatch = true,
            AllowCrm = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await store.CreateAsync(user, cancellationToken);
        logger.LogInformation("Založen první správce intranetu {Username}.", username);
    }
}
