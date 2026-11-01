namespace PHMTRANS.Dispatch;

public sealed class DispatchSchemaInitializer(SqlDispatchStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = store.TryInitializeAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}