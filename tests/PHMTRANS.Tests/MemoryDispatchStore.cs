using PHMTRANS.Dispatch;

namespace PHMTRANS.Tests;

public sealed class MemoryDispatchStore : IDispatchStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatchDocument _document = CreateSeed();

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

    private static DispatchDocument CreateSeed()
    {
        var now = DateTimeOffset.Now;
        return new DispatchDocument
        {
            Vehicles =
            [
                new("veh-1", "1A2 3456", "Cisterna MAN 32 m³", null, Compartments(8000, 8000, 6000, 6000, 4000)),
                new("veh-2", "2B3 4567", "Cisterna Volvo 28 m³", null, Compartments(7000, 7000, 7000, 7000)),
                new("veh-3", "3C4 5678", "Cisterna Scania 36 m³", null, Compartments(9000, 7000, 7000, 5000, 4000, 4000))
            ],
            Drivers =
            [
                new("drv-1", "Jan Novák", "+420 601 000 001"),
                new("drv-2", "Petr Svoboda", "+420 602 000 002"),
                new("drv-3", "Martin Dvořák", "+420 603 000 003")
            ],
            Trips =
            [
                Trip("trip-5", "J2026-0140", "Doprava Demo s.r.o.", "Sklad Mělník", "ČS Hradec Králové – ukázka", "motorová nafta", now.AddDays(-1), DispatchStatuses.Zrusena, null, null, "Ukázková jízda, zákazník zrušil", now.AddHours(-6)),
                Trip("trip-1", "J2026-0141", "Agropodnik Demo a.s.", "Sklad Lovosice", "ČS Olomouc – ukázka", "motorová nafta", now.AddHours(-8), DispatchStatuses.Vylozena, "veh-3", "drv-3", "Ukázková jízda", now.AddHours(-2)),
                Trip("trip-2", "J2026-0142", "Velkoobchod Palivo s.r.o.", "Sklad Cerekvice", "ČS Praha Chodov – ukázka", "motorová nafta", now.AddHours(-1), DispatchStatuses.NaCeste, "veh-1", "drv-1", "Ukázková jízda", now.AddMinutes(-30)),
                Trip("trip-6", "J2026-0139", "Storno Demo s.r.o.", "Sklad Kralupy", "ČS Kolín – ukázka", "motorová nafta", now.AddDays(-2), DispatchStatuses.Stornovano, null, null, "Ukázková jízda, storno", now.AddDays(-2)),
                Trip("trip-3", "J2026-0143", "Rafinérie Demo a.s.", "Areál Litvínov", "ČS Brno Slatina – ukázka", "benzin BA95", now.AddHours(3), DispatchStatuses.Nova, null, null, "Ukázková jízda, čeká na přiřazení", now),
                Trip("trip-4", "J2026-0144", "Síť stanic Demo s.r.o.", "Sklad Kralupy", "ČS Plzeň Bory – ukázka", "motorová nafta", now.AddHours(5), DispatchStatuses.Prirazena, "veh-2", "drv-2", "Ukázková jízda", now)
            ]
        };
    }

    private static VehicleCompartment[] Compartments(params decimal[] capacities) =>
        capacities.Select((capacity, index) => new VehicleCompartment(index + 1, capacity)).ToArray();

    private static DispatchTrip Trip(
        string id,
        string code,
        string customer,
        string loadingPlace,
        string unloadingPlace,
        string cargo,
        DateTimeOffset plannedStart,
        string status,
        string? vehicleId,
        string? driverId,
        string note,
        DateTimeOffset updatedAt) =>
        new(id, code, customer, loadingPlace, unloadingPlace, cargo, plannedStart, status, vehicleId, driverId, note, updatedAt);
}
