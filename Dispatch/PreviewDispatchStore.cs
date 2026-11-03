namespace PHMTRANS.Dispatch;

// Jen pro místní náhled bez SQL. V produkci se neregistruje.
public sealed class PreviewDispatchStore : IDispatchStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatchDocument _document = new()
    {
        Vehicles =
        [
            new("veh-1", "1A2 3456", "Cisterna MAN 32 m³"),
            new("veh-2", "2B3 4567", "Cisterna Volvo 28 m³")
        ],
        Drivers =
        [
            new("drv-1", "Jan Novák", "+420 601 000 001"),
            new("drv-2", "Petr Svoboda", "+420 602 000 002")
        ]
    };

    public async Task<DispatchSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return DispatchRules.Snapshot(_document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<DispatchMutation> AddTripAsync(NewTripRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => DispatchRules.AddTrip(document, request, now), cancellationToken);

    public Task<DispatchMutation> UpdateTripAsync(string id, UpdateTripRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => DispatchRules.UpdateTrip(document, id, request, now), cancellationToken);

    public Task<DispatchMutation> AssignAsync(string id, string? vehicleId, string? driverId, CancellationToken cancellationToken) =>
        Change((document, now) => DispatchRules.Assign(document, id, vehicleId, driverId, now), cancellationToken);

    public Task<DispatchMutation> SetStatusAsync(string id, string? status, CancellationToken cancellationToken) =>
        Change((document, now) => DispatchRules.SetStatus(document, id, status, now), cancellationToken);

    private async Task<DispatchMutation> Change(
        Func<DispatchDocument, DateTimeOffset, DispatchMutation> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return change(_document, DateTimeOffset.Now);
        }
        finally
        {
            _gate.Release();
        }
    }
}
