namespace PHMTRANS.Dispatch;

public interface IOrderStore
{
    Task<IReadOnlyList<DispatchOrder>> ListAsync(CancellationToken cancellationToken);

    Task<OrderBundle?> GetAsync(string id, CancellationToken cancellationToken);

    Task<OrderMutation> CreateAsync(OrderSaveRequest request, CancellationToken cancellationToken, string? updatedBy = null);

    Task<OrderMutation> UpdateAsync(string id, OrderSaveRequest request, CancellationToken cancellationToken, string? updatedBy = null);

    Task<IReadOnlyList<FuelTypeItem>> ListFuelTypesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LoadingPlaceItem>> ListLoadingPlacesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PointItem>> ListPointsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DistanceHit>> LookupDistancesAsync(IReadOnlyList<DistanceQuery> pairs, CancellationToken cancellationToken);

    Task SaveDistanceAsync(DistanceWrite write, string? updatedBy, CancellationToken cancellationToken);

    // missingDocumentsReason: proč chybí doklady, když se potvrzuje bez nich.
    Task<OrderMutation> ConfirmAsync(string id, CancellationToken cancellationToken, string? missingDocumentsReason = null);

    Task<OrderMutation> MarkInvoicedAsync(string id, string? invoiceNumber, CancellationToken cancellationToken);

    Task<OrderMutation> ResendAsync(string id, CancellationToken cancellationToken);

    Task<OrderMutation> CancelAsync(string id, string? reason, bool delete, CancellationToken cancellationToken);



    Task<IReadOnlyList<OrderSyncEntry>> ListQueueAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderSyncEntry>> ListDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    Task MarkSentAsync(OrderSyncEntry entry, PohodaImportResult result, CancellationToken cancellationToken);

    Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken);

    Task SavePayloadAsync(long id, string payloadXml, CancellationToken cancellationToken);

    Task MarkDeleteCommittedAsync(OrderSyncEntry entry, string error, string addOnlyXml, CancellationToken cancellationToken);
}
