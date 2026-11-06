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

    private static DispatchDocument CreateSeed()
    {
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
            ]
        };
    }

    private static VehicleCompartment[] Compartments(params decimal[] capacities) =>
        capacities.Select((capacity, index) => new VehicleCompartment(index + 1, capacity)).ToArray();
}
