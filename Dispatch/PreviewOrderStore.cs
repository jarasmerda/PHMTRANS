using Microsoft.Extensions.Options;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Dispatch;

public sealed class PreviewOrderStore(IDispatchStore dispatch, IOptions<PohodaOptions> options) : IOrderStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly DispatchOrder[] Samples = PreviewOrders.Seed();
    private readonly List<DispatchOrder> _orders = [.. Samples];
    private readonly List<DispatchOrder> _versions = [.. Samples];
    private readonly List<OrderSyncEntry> _queue = [];
    private long _nextQueueId;

    public Task<IReadOnlyList<DispatchOrder>> ListAsync(CancellationToken cancellationToken) =>
        With(async token =>
        {
            var catalog = OrderCatalogs.From(await dispatch.ReadAsync(token));
            var list = _orders.Select(order => OrderRoute.Display(order with
            {
                LatestSyncStatus = Latest(order.Id)?.Status,
                LatestSyncError = Latest(order.Id)?.LastError,
                LatestSyncWarning = Latest(order.Id)?.Warning
            }, catalog)).ToArray();
            return (IReadOnlyList<DispatchOrder>)list;
        }, cancellationToken);

    public Task<OrderBundle?> GetAsync(string id, CancellationToken cancellationToken) =>
        With(async token =>
        {
            var order = _orders.FirstOrDefault(item => item.Id == id);
            if (order is null)
                return (OrderBundle?)null;
            var catalog = OrderCatalogs.From(await dispatch.ReadAsync(token));
            var versions = _versions.Where(item => item.Id == id).OrderByDescending(item => item.CurrentVersion).ToArray();
            var sync = _queue.Where(item => item.OrderId == id).OrderByDescending(item => item.Id).ToArray();
            var latest = sync.FirstOrDefault();
            var shown = latest is null
                ? order
                : order with
                {
                    LatestSyncStatus = latest.Status,
                    LatestSyncError = latest.LastError,
                    LatestSyncWarning = latest.Warning
                };
            return (OrderBundle?)new OrderBundle(OrderRoute.Display(shown, catalog), versions, sync);
        }, cancellationToken);

    public Task<OrderMutation> CreateAsync(OrderSaveRequest request, CancellationToken cancellationToken) =>
        Change(null, (context, _) => OrderWorkflow.Create(request, context), cancellationToken);

    public Task<OrderMutation> UpdateAsync(string id, OrderSaveRequest request, CancellationToken cancellationToken) =>
        Change(id, (context, _) => OrderWorkflow.Save(request, context), cancellationToken);

    public Task<OrderMutation> ConfirmAsync(string id, CancellationToken cancellationToken) =>
        Change(id, (context, _) => OrderWorkflow.Confirm(context), cancellationToken);

    public Task<OrderMutation> MarkInvoicedAsync(string id, string? invoiceNumber, CancellationToken cancellationToken) =>
        Change(id, (context, _) => OrderWorkflow.MarkInvoiced(invoiceNumber, context), cancellationToken);

    public Task<OrderMutation> ResendAsync(string id, CancellationToken cancellationToken) =>
        Change(id, (context, latest) => OrderWorkflow.Resend(latest, context), cancellationToken);

    public Task<OrderMutation> CancelAsync(string id, string? reason, bool delete, CancellationToken cancellationToken) =>
        Change(id, (context, _) => OrderWorkflow.Cancel(reason, delete, context), cancellationToken);

    public Task<OrderMutation> AttachTripAsync(string orderId, string? tripId, bool onlyIfFree, CancellationToken cancellationToken) =>
        Change(orderId, (context, _) => OrderWorkflow.AssignTrip(tripId, onlyIfFree, context), cancellationToken);

    public async Task<string?> AttachOrdersAsync(
        string tripId,
        IReadOnlyList<string> orderIds,
        bool onlyIfFree,
        CancellationToken cancellationToken)
    {
        foreach (var orderId in orderIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var result = await AttachTripAsync(orderId, tripId, onlyIfFree, cancellationToken);
            if (result is OrderMutation.Rejected rejected)
                return rejected.Message;
            if (result is OrderMutation.NotFound)
                return "Zakázka neexistuje.";
        }

        return null;
    }

    public Task<IReadOnlyList<OrderSyncEntry>> ListQueueAsync(CancellationToken cancellationToken) =>
        With(token => Task.FromResult<IReadOnlyList<OrderSyncEntry>>(_queue.OrderByDescending(item => item.Id).ToArray()), cancellationToken);

    public Task<IReadOnlyList<OrderSyncEntry>> ListDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        With(token => Task.FromResult<IReadOnlyList<OrderSyncEntry>>(_queue
            .Where(item => item.Status == OrderQueueStatuses.Pending && OrderSyncLimits.IsDue(item.ProcessedUtc, item.Attempts, now))
            .OrderBy(item => item.Id)
            .Take(take)
            .ToArray()), cancellationToken);

    public Task MarkSentAsync(OrderSyncEntry entry, PohodaImportResult result, CancellationToken cancellationToken) =>
        With(token =>
        {
            var index = _queue.FindIndex(item => item.Id == entry.Id && item.Status == OrderQueueStatuses.Pending && item.VersionTo == entry.VersionTo);
            if (index < 0)
                return Task.FromResult(0);

            _queue[index] = _queue[index] with
            {
                Status = OrderQueueStatuses.Sent,
                ProcessedUtc = DateTimeOffset.UtcNow,
                PohodaDocNumber = result.Number,
                LastError = null
            };
            var orderIndex = _orders.FindIndex(item => item.Id == entry.OrderId);
            if (orderIndex >= 0)
            {
                var order = _orders[orderIndex];
                _orders[orderIndex] = order with
                {
                    LastSentVersion = entry.VersionTo,
                    PohodaOrderId = result.Id ?? order.PohodaOrderId,
                    PohodaOrderNumber = result.Number ?? order.PohodaOrderNumber
                };
            }

            return Task.FromResult(0);
        }, cancellationToken);

    public Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken) =>
        With(token =>
        {
            var index = _queue.FindIndex(item => item.Id == id && item.Status == OrderQueueStatuses.Pending);
            if (index < 0)
                return Task.FromResult(0);
            var current = _queue[index];
            var attempts = current.Attempts + 1;
            _queue[index] = current with
            {
                Attempts = attempts,
                LastError = OrderSyncLimits.TrimToColumn(error, OrderSyncLimits.LastErrorMaxLength),
                ProcessedUtc = DateTimeOffset.UtcNow,
                Status = attempts >= OrderSyncLimits.MaxAttempts ? OrderQueueStatuses.Failed : OrderQueueStatuses.Pending
            };
            return Task.FromResult(0);
        }, cancellationToken);

    public Task SavePayloadAsync(long id, string payloadXml, CancellationToken cancellationToken) =>
        With(token =>
        {
            var index = _queue.FindIndex(item => item.Id == id && item.Status == OrderQueueStatuses.Pending);
            if (index >= 0)
                _queue[index] = _queue[index] with { PayloadXml = payloadXml };
            return Task.FromResult(0);
        }, cancellationToken);

    public Task MarkDeleteCommittedAsync(OrderSyncEntry entry, string error, string addOnlyXml, CancellationToken cancellationToken) =>
        With(token =>
        {
            var index = _queue.FindIndex(item => item.Id == entry.Id && item.Status == OrderQueueStatuses.Pending);
            if (index < 0)
                return Task.FromResult(0);
            var current = _queue[index];
            var attempts = current.Attempts + 1;
            _queue[index] = current with
            {
                PayloadXml = addOnlyXml,
                Attempts = attempts,
                LastError = OrderSyncLimits.TrimToColumn(error, OrderSyncLimits.LastErrorMaxLength),
                ProcessedUtc = DateTimeOffset.UtcNow,
                Status = attempts >= OrderSyncLimits.MaxAttempts ? OrderQueueStatuses.Failed : OrderQueueStatuses.Pending
            };
            var orderIndex = _orders.FindIndex(item => item.Id == entry.OrderId);
            if (orderIndex >= 0)
                _orders[orderIndex] = _orders[orderIndex] with { PohodaOrderId = null };
            return Task.FromResult(0);
        }, cancellationToken);

    private async Task<OrderMutation> Change(
        string? id,
        Func<OrderEditContext, OrderSyncEntry?, OrderChange> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = OrderCatalogs.From(await dispatch.ReadAsync(cancellationToken));
            DispatchOrder? current = null;
            DispatchOrder? lastSent = null;
            OrderSyncEntry? latest = null;
            if (id is not null)
            {
                current = _orders.FirstOrDefault(item => item.Id == id);
                if (current is null)
                    return new OrderMutation.NotFound();
                if (current.LastSentVersion is int version)
                    lastSent = _versions.FirstOrDefault(item => item.Id == id && item.CurrentVersion == version);
                latest = _queue.Where(item => item.OrderId == id).OrderByDescending(item => item.Id).FirstOrDefault();
            }

            var context = new OrderEditContext
            {
                Catalog = catalog,
                SyncMode = options.Value.EffectiveSyncMode(),
                Pohoda = options.Value,
                Now = DateTimeOffset.Now,
                ExistingCodes = _orders.Select(item => item.Code).ToArray(),
                Current = current,
                LastSent = lastSent,
                SentCorrections = id is null
                    ? 0
                    : _queue.Count(item => item.OrderId == id && OrderSyncActions.CountsTowardCorrectionIndex(item.Action) && item.Status == OrderQueueStatuses.Sent)
            };
            var result = change(context, latest);
            if (result is OrderChange.Deleted deleted)
            {
                _orders.RemoveAll(item => item.Id == deleted.Id);
                _versions.RemoveAll(item => item.Id == deleted.Id);
                _queue.RemoveAll(item => item.OrderId == deleted.Id);
                return new OrderMutation.Deleted(deleted.Id);
            }

            if (result is not OrderChange.Saved saved)
                return new OrderMutation.Rejected(result is OrderChange.Rejected rejected ? rejected.Message : "Zakázku se nepodařilo uložit.");

            var order = saved.Queue is null
                ? saved.Order
                : saved.Order with { LatestSyncStatus = saved.Queue.Status, LatestSyncWarning = saved.Queue.Warning };
            var index = _orders.FindIndex(item => item.Id == order.Id);
            if (index < 0)
                _orders.Add(order);
            else
                _orders[index] = order;
            if (saved.VersionAdded)
                _versions.Add(order);
            if (saved.DropOpenQueue)
                DropOpen(order.Id);
            if (saved.Queue is not null)
                Upsert(order, saved.Queue);
            return new OrderMutation.Success(_orders.First(item => item.Id == order.Id));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DropOpen(string orderId)
    {
        for (var index = 0; index < _queue.Count; index++)
        {
            if (_queue[index].OrderId != orderId || !OrderQueueStatuses.IsOpen(_queue[index].Status))
                continue;
            _queue[index] = _queue[index] with
            {
                Status = OrderQueueStatuses.Failed,
                LastError = "Zakázka byla stornována před odesláním do Pohody.",
                ProcessedUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private void Upsert(DispatchOrder order, QueueUpsert queue)
    {
        var target = queue.TargetId;
        if (target is null)
            target = _queue.LastOrDefault(item => item.OrderId == order.Id && OrderQueueStatuses.IsOpen(item.Status))?.Id;

        if (target is null)
        {
            _queue.Add(new OrderSyncEntry(
                ++_nextQueueId,
                order.Id,
                order.Code,
                queue.VersionFrom,
                queue.VersionTo,
                queue.Action,
                queue.PayloadXml,
                queue.Status,
                0,
                null,
                DateTimeOffset.UtcNow,
                null,
                null,
                queue.Warning));
            return;
        }

        var index = _queue.FindIndex(item => item.Id == target.Value);
        if (index < 0)
            return;
        var current = _queue[index];
        _queue[index] = current with
        {
            VersionFrom = queue.VersionFrom,
            VersionTo = queue.VersionTo,
            Action = queue.Action,
            PayloadXml = queue.PayloadXml,
            Status = queue.Status,
            Warning = queue.Warning,
            Attempts = queue.ResetAttempts ? 0 : current.Attempts,
            LastError = queue.ResetAttempts ? null : current.LastError,
            ProcessedUtc = queue.ResetAttempts ? null : current.ProcessedUtc
        };
    }

    private OrderSyncEntry? Latest(string orderId) =>
        _queue.Where(item => item.OrderId == orderId).OrderByDescending(item => item.Id).FirstOrDefault();

    private async Task<T> With<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class PreviewOrders
{
    public static DispatchOrder[] Seed()
    {
        var now = DateTimeOffset.Parse("2026-11-04T06:00:00+01:00");
        return
        [
            Order(
                "ord-preview-1",
                "Z2026-0301",
                "Síť stanic Demo s.r.o.",
                "Sklad Kralupy",
                "motorová nafta",
                30000m,
                OrderPricing.ByKm,
                salePerKm: 46m,
                salePerLiter: 1.1m,
                costPerKm: 31.5m,
                "trip-preview",
                "veh-1",
                "drv-1",
                now,
                [
                    Stop("st-1", 1, "ČS Brno Slatina", 8000m),
                    Stop("st-2", 2, "ČS Olomouc", 10000m),
                    Stop("st-3", 3, "ČS Plzeň Bory", 12000m)
                ],
                [
                    Leg("lg-1", 1, "Sklad Kralupy", "ČS Brno Slatina", 8000m, 180m, false, 0m, OrderPricing.ByKm, 46m, 1.1m, 31.5m),
                    Leg("lg-2", 2, "ČS Brno Slatina", "ČS Olomouc", 10000m, 140m, false, 0m, OrderPricing.ByKm, 46m, 1.1m, 31.5m),
                    Leg("lg-3", 3, "ČS Olomouc", "ČS Plzeň Bory", 12000m, 160m, false, 0m, OrderPricing.ByKm, 46m, 1.1m, 31.5m)
                ]),
            Order(
                "ord-preview-2",
                "Z2026-0302",
                "Agropodnik Demo a.s.",
                "Sklad Kralupy",
                "motorová nafta",
                16000m,
                OrderPricing.ByLiters,
                salePerKm: 40m,
                salePerLiter: 1.25m,
                costPerKm: 31.5m,
                "trip-preview",
                "veh-1",
                "drv-1",
                now,
                [
                    Stop("st-4", 1, "ČS Hradec Králové", 9000m),
                    Stop("st-5", 2, "ČS Liberec", 7000m)
                ],
                [
                    Leg("lg-4", 1, "Sklad Kralupy", "ČS Hradec Králové", 9000m, 90m, false, 0m, OrderPricing.ByLiters, 40m, 1.25m, 31.5m),
                    Leg("lg-5", 2, "ČS Hradec Králové", "ČS Liberec", 7000m, 70m, false, 0m, OrderPricing.ByLiters, 40m, 1.25m, 31.5m)
                ]),
            Order(
                "ord-preview-3",
                "Z2026-0303",
                "Volná zakázka s.r.o.",
                "Sklad Mělník",
                "benzin BA95",
                24000m,
                OrderPricing.ByKm,
                salePerKm: 42m,
                salePerLiter: 0.9m,
                costPerKm: 28m,
                null,
                null,
                null,
                now,
                [Stop("st-6", 1, "ČS České Budějovice", 24000m)],
                [Leg("lg-6", 1, "Sklad Mělník", "ČS České Budějovice", 24000m, 150m, false, 0m, OrderPricing.ByKm, 42m, 0.9m, 28m)])
                with { CustomerPohodaId = null },
            Order(
                "ord-preview-4",
                "Z2026-0304",
                "Rafinérie Demo a.s.",
                "Areál Litvínov",
                "motorová nafta",
                18000m,
                OrderPricing.ByKm,
                salePerKm: 44m,
                salePerLiter: 1m,
                costPerKm: 29m,
                null,
                null,
                null,
                now,
                [Stop("st-7", 1, "ČS Plzeň Bory", 18000m)],
                [Leg("lg-7", 1, "Areál Litvínov", "ČS Plzeň Bory", 18000m, 120m, false, 0m, OrderPricing.ByKm, 44m, 1m, 29m)])
        ];
    }

    private static OrderStop Stop(string id, int sequence, string place, decimal liters) =>
        new(id, sequence, place, liters, null, null);

    private static OrderLeg Leg(
        string id,
        int sequence,
        string from,
        string to,
        decimal liters,
        decimal kilometers,
        bool overridden,
        decimal overridePrice,
        string mode,
        decimal salePerKm,
        decimal salePerLiter,
        decimal costPerKm) =>
        new(
            id,
            sequence,
            from,
            to,
            liters,
            kilometers,
            OrderPricing.Sale(kilometers, liters, overridden, overridePrice, mode, salePerKm, salePerLiter),
            overridden,
            OrderPricing.Cost(kilometers, costPerKm));

    private static DispatchOrder Order(
        string id,
        string code,
        string customer,
        string loading,
        string fuel,
        decimal liters,
        string mode,
        decimal salePerKm,
        decimal salePerLiter,
        decimal costPerKm,
        string? tripId,
        string? vehicleId,
        string? driverId,
        DateTimeOffset now,
        IReadOnlyList<OrderStop> stops,
        IReadOnlyList<OrderLeg> legs)
    {
        var agreed = OrderPohodaFields.Money(legs.Sum(leg => leg.SalePrice));
        var order = new DispatchOrder(
            id,
            code,
            42,
            customer,
            loading,
            stops[^1].Place,
            DateOnly.FromDateTime(now.DateTime),
            DateOnly.FromDateTime(now.DateTime),
            fuel,
            liters,
            agreed,
            0m,
            null,
            null,
            vehicleId,
            driverId,
            tripId,
            OrderStatuses.Naplanovana,
            false,
            1,
            null,
            null,
            null,
            null,
            now,
            now,
            null,
            null,
            null,
            mode,
            salePerKm,
            salePerLiter,
            costPerKm,
            stops,
            legs);
        return order with { Quote = OrderPricing.Summarize(order) };
    }
}
