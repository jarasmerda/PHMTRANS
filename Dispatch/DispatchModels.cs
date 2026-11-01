namespace PHMTRANS.Dispatch;

public static class DispatchStatuses
{
    public const string Nova = "Nová";
    public const string Prirazena = "Přiřazená";
    public const string NaCeste = "Na cestě";
    public const string Vylozena = "Vyložená";
    public const string Zrusena = "Zrušená";

    public const string UnknownMessage =
        "Neznámý stav. Povolené jsou Nová, Přiřazená, Na cestě, Vyložená a Zrušená.";

    public static readonly IReadOnlyList<string> All = [Nova, Prirazena, NaCeste, Vylozena, Zrusena];

    public static bool IsKnown(string status) => All.Contains(status);

    public static int Rank(string status) => status switch
    {
        NaCeste => 0,
        Prirazena => 1,
        Nova => 2,
        Vylozena => 3,
        Zrusena => 4,
        _ => 5
    };
}

public sealed record DispatchVehicle(string Id, string Plate, string Label);

public sealed record DispatchDriver(string Id, string Name, string? Phone);

public sealed record DispatchTrip(
    string Id,
    string Code,
    string? OrderCode,
    string Customer,
    string LoadingPlace,
    string UnloadingPlace,
    string Cargo,
    DateTimeOffset PlannedStart,
    string Status,
    string? VehicleId,
    string? DriverId,
    string? Note,
    DateTimeOffset UpdatedAt);

public sealed class DispatchDocument
{
    public int Version { get; set; } = 1;

    public string Notice { get; set; } = DispatchStorage.Note;

    public List<DispatchVehicle> Vehicles { get; set; } = [];

    public List<DispatchDriver> Drivers { get; set; } = [];

    public List<DispatchTrip> Trips { get; set; } = [];
}

public sealed record DispatchSnapshot(
    IReadOnlyList<DispatchTrip> Trips,
    IReadOnlyList<DispatchVehicle> Vehicles,
    IReadOnlyList<DispatchDriver> Drivers);

public sealed record DispatchStatusCount(string Status, int Count);

public sealed record DispatchBoard(
    string Storage,
    string StorageNote,
    IReadOnlyList<DispatchStatusCount> Counts,
    IReadOnlyList<DispatchTrip> Trips,
    IReadOnlyList<DispatchVehicle> Vehicles,
    IReadOnlyList<DispatchDriver> Drivers);

public sealed record NewTripRequest(
    string? OrderCode,
    string? Customer,
    string? LoadingPlace,
    string? UnloadingPlace,
    string? Cargo,
    DateTimeOffset? PlannedStart,
    string? Note,
    string? VehicleId = null,
    string? DriverId = null);

public sealed record UpdateTripRequest(
    string? OrderCode,
    string? Customer,
    string? LoadingPlace,
    string? UnloadingPlace,
    string? Cargo,
    DateTimeOffset? PlannedStart,
    string? Note,
    string? VehicleId = null,
    string? DriverId = null,
    string? Status = null);

public sealed record AssignTripRequest(string? VehicleId, string? DriverId);

public sealed record ChangeStatusRequest(string? Status);

public abstract record DispatchMutation
{
    private DispatchMutation()
    {
    }

    public sealed record Success(DispatchTrip Trip) : DispatchMutation;

    public sealed record NotFound : DispatchMutation;

    public sealed record Rejected(string Message) : DispatchMutation;
}

public sealed class DispatchStoreException(string message, Exception? inner = null) : Exception(message, inner);
