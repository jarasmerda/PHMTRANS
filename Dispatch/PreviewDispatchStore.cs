namespace PHMTRANS.Dispatch;

// Jen pro místní náhled bez SQL. V produkci se neregistruje.
public sealed class PreviewDispatchStore : IDispatchStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatchDocument _document = new()
    {
        Vehicles =
        [
            new("veh-1", "1A2 3456", "Cisterna MAN 32 m³", 31.5m),
            new("veh-2", "2B3 4567", "Cisterna Volvo 28 m³", 29m)
        ],
        Drivers =
        [
            new("drv-1", "Jan Novák", "+420 601 000 001"),
            new("drv-2", "Petr Svoboda", "+420 602 000 002")
        ],
        Trips =
        [
            new(
                "trip-preview",
                "J2026-0201",
                "Síť stanic Demo s.r.o.",
                "Sklad Kralupy",
                "ČS Plzeň Bory",
                "motorová nafta",
                DateTimeOffset.Now.AddHours(4),
                DispatchStatuses.Prirazena,
                "veh-1",
                "drv-1",
                "Rozvoz na víc vykládek",
                DateTimeOffset.Now)
        ],
        DefaultCostPerKm = 28m
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

    public Task<DispatchMutation> CancelTripAsync(string id, CancellationToken cancellationToken) =>
        Change((document, now) => DispatchRules.CancelTrip(document, id, now), cancellationToken);

    public Task<DispatchMutation> DeleteTripAsync(string id, int orderCount, CancellationToken cancellationToken) =>
        Change((document, now) => DispatchRules.DeleteTrip(document, id, orderCount), cancellationToken);

    public async Task<DispatchRateCard> GetRatesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return DispatchRules.Rates(_document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> SaveRatesAsync(
        decimal defaultCostPerKm,
        IReadOnlyList<VehicleCostUpdate> vehicles,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return DispatchRules.SaveRates(_document, defaultCostPerKm, vehicles);
        }
        finally
        {
            _gate.Release();
        }
    }

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
