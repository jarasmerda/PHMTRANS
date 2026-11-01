namespace PHMTRANS.Crm;

public sealed class CrmSchemaInitializer(SqlCrmStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = store.TryInitializeAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
