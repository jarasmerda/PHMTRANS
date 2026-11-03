using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PHMTRANS.Auth;

public sealed class AuthSchemaInitializer(
    IAuthStore store,
    IOptions<IntranetOptions> options,
    IPasswordHasher<AuthUser> hasher,
    ILogger<AuthSchemaInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.TryInitializeAsync(cancellationToken);
            await AuthBootstrap.EnsureAdminAsync(store, options.Value, hasher, logger, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Účty intranetu se nepodařilo připravit. Aplikace běží dál, přihlášení nemusí být dostupné.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
