using Microsoft.Extensions.Options;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Dispatch;

public sealed class OrderSyncWorker(
    IOrderStore store,
    PohodaMServerClient pohoda,
    IOptions<PohodaOptions> options,
    ILogger<OrderSyncWorker> logger) : BackgroundService
{
    private const int ResponseLogLimit = 4000;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mode = options.Value.EffectiveSyncMode();
        logger.LogInformation("Synchronizace zakázek do Pohody: {Mode}.", mode);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Zpracování fronty zakázek do Pohody selhalo.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (options.Value.EffectiveSyncMode() != "Live")
            return;

        var due = await store.ListDueAsync(DateTimeOffset.UtcNow, 10, cancellationToken);
        foreach (var entry in due)
        {
            try
            {
                var bundle = await store.GetAsync(entry.OrderId, cancellationToken);
                if (bundle is null)
                {
                    await store.MarkFailedAsync(entry.Id, "Zakázka už neexistuje.", cancellationToken);
                    continue;
                }

                if (entry.Action == OrderSyncActions.Create && !OrderStatuses.IsConfirmed(bundle.Order.Status))
                {
                    await store.MarkFailedAsync(entry.Id, "Zakázka už není potvrzená k fakturaci.", cancellationToken);
                    continue;
                }

                var source = DocumentFor(bundle, entry);
                var lastSent = bundle.Order.LastSentVersion is int sentVersion
                    ? bundle.Versions.FirstOrDefault(version => version.CurrentVersion == sentVersion)
                    : null;
                var sentCorrections = bundle.Sync.Count(item =>
                    item.Action == OrderSyncActions.Correction && item.Status == OrderQueueStatuses.Sent);
                var current = entry;
                var payload = OrderPohodaXml.EnsureCurrent(options.Value, current, source, lastSent, sentCorrections);
                if (!string.Equals(payload, current.PayloadXml, StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "Fronta zakázky {OrderCode} má zastaralé XML ({Action}), posílá se aktuální požadavek.",
                        current.OrderCode,
                        current.Action);
                    await store.SavePayloadAsync(current.Id, payload, cancellationToken);
                    current = current with { PayloadXml = payload };
                }

                logger.LogDebug("XML požadavku pro zakázku {OrderCode}: {PayloadXml}", current.OrderCode, current.PayloadXml);
                var response = await pohoda.ImportXmlAsync(current.PayloadXml, cancellationToken);
                var result = OrderPohodaXml.ReadImport(response.Body);
                if (!result.Ok)
                {
                    logger.LogWarning("Pohoda odmítla zakázku {OrderCode}: {Error}", entry.OrderCode, result.Error);
                    logger.LogWarning(
                        "Odpověď Pohody po odmítnutí zakázky {OrderCode}: {Body}",
                        entry.OrderCode,
                        OrderSyncLimits.Cut(response.Body, ResponseLogLimit));
                    if (result.DeleteCommitted)
                    {
                        var addOnly = OrderPohodaXml.AddOnly(options.Value, current, source, lastSent, sentCorrections);
                        await store.MarkDeleteCommittedAsync(
                            current,
                            result.Error ?? "Smazání v Pohodě proběhlo, založení nové verze selhalo.",
                            addOnly,
                            cancellationToken);
                    }
                    else
                    {
                        await store.MarkFailedAsync(current.Id, result.Error ?? "Pohoda vrátila chybu.", cancellationToken);
                    }

                    continue;
                }

                await store.MarkSentAsync(current, result, cancellationToken);
                logger.LogInformation(
                    "Zakázka {OrderCode} verze {Version} ({Action}) je v Pohodě jako {Number}.",
                    entry.OrderCode,
                    entry.VersionTo,
                    entry.Action,
                    result.Number);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Odeslání zakázky {OrderCode} do Pohody selhalo.", entry.OrderCode);
                try
                {
                    await store.MarkFailedAsync(entry.Id, exception.Message, cancellationToken);
                }
                catch (Exception markException) when (markException is not OperationCanceledException)
                {
                    logger.LogError(markException, "Chybu odeslání zakázky {OrderCode} se nepodařilo zapsat do fronty.", entry.OrderCode);
                }
            }
        }
    }

    private static DispatchOrder DocumentFor(OrderBundle bundle, OrderSyncEntry entry)
    {
        var version = bundle.Versions.FirstOrDefault(item => item.CurrentVersion == entry.VersionTo) ?? bundle.Order;
        return version with
        {
            Code = bundle.Order.Code,
            PohodaOrderId = bundle.Order.PohodaOrderId,
            PohodaOrderNumber = bundle.Order.PohodaOrderNumber
        };
    }
}
