using Dapper;
using Microsoft.Data.SqlClient;

namespace PHMTRANS.Auth;

public sealed class SqlAuthStore(IConfiguration configuration, ILogger<SqlAuthStore> logger) : IAuthStore
{
    private readonly string? _connectionString = configuration.GetConnectionString("Dispatch");
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private int _schemaReady;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_connectionString);

    public async Task TryInitializeAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            logger.LogWarning("ConnectionStrings:Dispatch není nastavené. Přihlášení intranetu není dostupné, aplikace běží dál.");
            return;
        }

        try
        {
            await EnsureReadyAsync(cancellationToken);
            logger.LogInformation("Schéma účtů (tabulka Auth_User) je připravené.");
        }
        catch (AuthStoreException)
        {
            // EnsureReadyAsync už chybu zapsalo. Start služby kvůli tomu nesmí spadnout.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Schéma účtů se nepodařilo založit. Aplikace běží dál.");
        }
    }

    public Task<AuthUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            return await connection.QuerySingleOrDefaultAsync<AuthUser>(new CommandDefinition(
                """
                SELECT Id, Username, DisplayName, PasswordHash, IsAdmin, IsActive, AllowErp, AllowDispatch, AllowCrm,
                       ErpReports, CreatedAt, LastLoginAt, FailedAttempts, LockoutUntil
                FROM dbo.Auth_User
                WHERE LOWER(Username) = LOWER(@Username)
                """,
                new { Username = username },
                cancellationToken: token));
        }, cancellationToken);

    public Task<AuthUser?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            return await connection.QuerySingleOrDefaultAsync<AuthUser>(new CommandDefinition(
                """
                SELECT Id, Username, DisplayName, PasswordHash, IsAdmin, IsActive, AllowErp, AllowDispatch, AllowCrm,
                       ErpReports, CreatedAt, LastLoginAt, FailedAttempts, LockoutUntil
                FROM dbo.Auth_User
                WHERE Id = @Id
                """,
                new { Id = id },
                cancellationToken: token));
        }, cancellationToken);

    public Task<IReadOnlyList<AuthUser>> ListAsync(CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            var rows = await connection.QueryAsync<AuthUser>(new CommandDefinition(
                """
                SELECT Id, Username, DisplayName, PasswordHash, IsAdmin, IsActive, AllowErp, AllowDispatch, AllowCrm,
                       ErpReports, CreatedAt, LastLoginAt, FailedAttempts, LockoutUntil
                FROM dbo.Auth_User
                ORDER BY Username
                """,
                cancellationToken: token));
            return (IReadOnlyList<AuthUser>)rows.ToArray();
        }, cancellationToken);

    public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.Auth_User WHERE IsAdmin = 1 AND IsActive = 1",
                cancellationToken: token));
        }, cancellationToken);

    public Task<bool> AnyAdminAsync(CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.Auth_User WHERE IsAdmin = 1",
                cancellationToken: token));
            return count > 0;
        }, cancellationToken);

    public Task<AuthUser> CreateAsync(AuthUser user, CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO dbo.Auth_User
                        (Id, Username, DisplayName, PasswordHash, IsAdmin, IsActive, AllowErp, AllowDispatch, AllowCrm,
                         ErpReports, CreatedAt, LastLoginAt, FailedAttempts, LockoutUntil)
                    VALUES
                        (@Id, @Username, @DisplayName, @PasswordHash, @IsAdmin, @IsActive, @AllowErp, @AllowDispatch, @AllowCrm,
                         @ErpReports, @CreatedAt, @LastLoginAt, @FailedAttempts, @LockoutUntil)
                    """,
                    user,
                    cancellationToken: token));
            }
            catch (SqlException exception) when (exception.Number is 2601 or 2627)
            {
                throw new AuthStoreException(AuthStorage.DuplicateUserMessage, exception);
            }

            return user;
        }, cancellationToken);

    public Task UpdateAsync(AuthUser user, CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.Auth_User
                SET DisplayName = @DisplayName,
                    IsAdmin = @IsAdmin,
                    IsActive = @IsActive,
                    AllowErp = @AllowErp,
                    AllowDispatch = @AllowDispatch,
                    AllowCrm = @AllowCrm,
                    ErpReports = @ErpReports
                WHERE Id = @Id
                """,
                user,
                cancellationToken: token));
            return 0;
        }, cancellationToken);

    public Task SetPasswordHashAsync(Guid id, string passwordHash, CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.Auth_User
                SET PasswordHash = @PasswordHash, FailedAttempts = 0, LockoutUntil = NULL
                WHERE Id = @Id
                """,
                new { Id = id, PasswordHash = passwordHash },
                cancellationToken: token));
            return 0;
        }, cancellationToken);

    public Task SetFailuresAsync(Guid id, int failedAttempts, DateTimeOffset? lockoutUntil, CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.Auth_User
                SET FailedAttempts = @FailedAttempts, LockoutUntil = @LockoutUntil
                WHERE Id = @Id
                """,
                new { Id = id, FailedAttempts = failedAttempts, LockoutUntil = lockoutUntil },
                cancellationToken: token));
            return 0;
        }, cancellationToken);

    public Task RecordLoginAsync(Guid id, CancellationToken cancellationToken) =>
        InConnection(async (connection, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.Auth_User
                SET LastLoginAt = @LastLoginAt, FailedAttempts = 0, LockoutUntil = NULL
                WHERE Id = @Id
                """,
                new { Id = id, LastLoginAt = DateTimeOffset.UtcNow },
                cancellationToken: token));
            return 0;
        }, cancellationToken);

    private async Task<T> InConnection<T>(
        Func<SqlConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken);
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return await action(connection, cancellationToken);
        }
        catch (AuthStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            logger.LogError(exception, "Účty intranetu se nepřipojily k SQL Serveru.");
            throw new AuthStoreException(AuthStorage.UnavailableMessage, exception);
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new AuthStoreException(AuthStorage.UnavailableMessage);

        if (Volatile.Read(ref _schemaReady) == 1)
            return;

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady == 1)
                return;

            await AuthSchema.EnsureAsync(_connectionString, cancellationToken);
            Volatile.Write(ref _schemaReady, 1);
        }
        catch (AuthStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Účty intranetu se nepřipojily k SQL Serveru.");
            throw new AuthStoreException(AuthStorage.UnavailableMessage, exception);
        }
        finally
        {
            _schemaGate.Release();
        }
    }
}
