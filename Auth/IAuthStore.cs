namespace PHMTRANS.Auth;

public interface IAuthStore
{
    bool IsAvailable { get; }

    Task TryInitializeAsync(CancellationToken cancellationToken);

    Task<AuthUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    Task<AuthUser?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthUser>> ListAsync(CancellationToken cancellationToken);

    Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken);

    Task<bool> AnyAdminAsync(CancellationToken cancellationToken);

    Task<AuthUser> CreateAsync(AuthUser user, CancellationToken cancellationToken);

    Task UpdateAsync(AuthUser user, CancellationToken cancellationToken);

    Task SetPasswordHashAsync(Guid id, string passwordHash, CancellationToken cancellationToken);

    Task SetFailuresAsync(Guid id, int failedAttempts, DateTimeOffset? lockoutUntil, CancellationToken cancellationToken);

    Task RecordLoginAsync(Guid id, CancellationToken cancellationToken);
}
