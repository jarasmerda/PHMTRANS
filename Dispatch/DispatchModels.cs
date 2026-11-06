namespace PHMTRANS.Dispatch;

public sealed record DispatchVehicle(
    string Id,
    string Plate,
    string Label,
    decimal? CostPerKm = null,
    IReadOnlyList<VehicleCompartment>? Compartments = null);

// Přepážka cisterny. Čísluje se od 1, vozidlo jich má nejvýš 10.
public sealed record VehicleCompartment(int Number, decimal CapacityLiters);

public static class VehicleCompartments
{
    public const int MaxCount = 10;

    public static decimal TotalCapacity(DispatchVehicle vehicle) =>
        (vehicle.Compartments ?? []).Sum(compartment => compartment.CapacityLiters);
}

public sealed record DispatchDriver(string Id, string Name, string? Phone);

public sealed class DispatchDocument
{
    public int Version { get; set; } = 1;

    public string Notice { get; set; } = DispatchStorage.Note;

    public List<DispatchVehicle> Vehicles { get; set; } = [];

    public List<DispatchDriver> Drivers { get; set; } = [];

    public decimal DefaultCostPerKm { get; set; }
}

public sealed record DispatchSnapshot(
    IReadOnlyList<DispatchVehicle> Vehicles,
    IReadOnlyList<DispatchDriver> Drivers,
    decimal DefaultCostPerKm = 0m);

public sealed record DispatchBoard(
    string Storage,
    string StorageNote,
    IReadOnlyList<DispatchVehicle> Vehicles,
    IReadOnlyList<DispatchDriver> Drivers);

public sealed record VehicleCostUpdate(string? Id, decimal? CostPerKm);

public sealed record DispatchRateCard(decimal DefaultCostPerKm, IReadOnlyList<DispatchVehicle> Vehicles);

public sealed record SaveRatesRequest(decimal? DefaultCostPerKm, IReadOnlyList<VehicleCostUpdate>? Vehicles);

public sealed class DispatchStoreException(string message, Exception? inner = null) : Exception(message, inner);
