namespace PHMTRANS.Dispatch;

public interface IOrderStore
{
    Task<IReadOnlyList<DispatchOrder>> ListAsync(CancellationToken cancellationToken);

    Task<OrderBundle?> GetAsync(string id, CancellationToken cancellationToken);

    Task<OrderMutation> CreateAsync(OrderSaveRequest request, CancellationToken cancellationToken);

    Task<OrderMutation> UpdateAsync(string id, OrderSaveRequest request, CancellationToken cancellationToken);

    Task<OrderMutation> ConfirmAsync(string id, CancellationToken cancellationToken);

    Task<OrderMutation> MarkInvoicedAsync(string id, string? invoiceNumber, CancellationToken cancellationToken);

    Task<OrderMutation> ResendAsync(string id, CancellationToken cancellationToken);

    Task<OrderMutation> CancelAsync(string id, string? reason, bool delete, CancellationToken cancellationToken);

    Task<OrderMutation> AttachTripAsync(string orderId, string? tripId, bool onlyIfFree, CancellationToken cancellationToken);

    Task<string?> AttachOrdersAsync(string tripId, IReadOnlyList<string> orderIds, bool onlyIfFree, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderSyncEntry>> ListQueueAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderSyncEntry>> ListDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    Task MarkSentAsync(OrderSyncEntry entry, PohodaImportResult result, CancellationToken cancellationToken);

    Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken);

    Task SavePayloadAsync(long id, string payloadXml, CancellationToken cancellationToken);

    Task MarkDeleteCommittedAsync(OrderSyncEntry entry, string error, string addOnlyXml, CancellationToken cancellationToken);
}
