using PHMTRANS.Auth;

namespace PHMTRANS.Tests;

public sealed class MemoryAuthStore : IAuthStore
{
    private readonly Lock _gate = new();
    private readonly List<AuthUser> _users = [];

    public bool IsAvailable { get; set; } = true;

    public Task TryInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<AuthUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_users.FirstOrDefault(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<AuthUser?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_users.FirstOrDefault(user => user.Id == id));
    }

    public Task<IReadOnlyList<AuthUser>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<AuthUser>>(_users.OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_users.Count(user => user.IsAdmin && user.IsActive));
    }

    public Task<bool> AnyAdminAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_users.Any(user => user.IsAdmin));
    }

    public Task<AuthUser> CreateAsync(AuthUser user, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_users.Any(item => item.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)))
                throw new AuthStoreException(AuthStorage.DuplicateUserMessage);

            _users.Add(user);
            return Task.FromResult(user);
        }
    }

    public Task UpdateAsync(AuthUser user, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _users.FindIndex(item => item.Id == user.Id);
            if (index < 0)
                throw new AuthStoreException("Uživatel neexistuje.");

            _users[index] = user;
            return Task.CompletedTask;
        }
    }

    public Task SetPasswordHashAsync(Guid id, string passwordHash, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var user = Required(id);
            user.PasswordHash = passwordHash;
            user.FailedAttempts = 0;
            user.LockoutUntil = null;
            return Task.CompletedTask;
        }
    }

    public Task SetFailuresAsync(Guid id, int failedAttempts, DateTimeOffset? lockoutUntil, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var user = Required(id);
            user.FailedAttempts = failedAttempts;
            user.LockoutUntil = lockoutUntil;
            return Task.CompletedTask;
        }
    }

    public Task RecordLoginAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var user = Required(id);
            user.LastLoginAt = DateTimeOffset.UtcNow;
            user.FailedAttempts = 0;
            user.LockoutUntil = null;
            return Task.CompletedTask;
        }
    }

    private AuthUser Required(Guid id) =>
        _users.FirstOrDefault(user => user.Id == id) ?? throw new AuthStoreException("Uživatel neexistuje.");
}
