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

    Task<IReadOnlyList<OrderSyncEntry>> ListQueueAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderSyncEntry>> ListDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    Task MarkSentAsync(OrderSyncEntry entry, PohodaImportResult result, CancellationToken cancellationToken);

    Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken);
}
