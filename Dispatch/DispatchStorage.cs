namespace PHMTRANS.Dispatch;

public static class DispatchStorage
{
    public const string Kind = "sql";

    public const string Note =
        "Jízdy, vozidla, řidiči a zakázky jsou v SQL Serveru (tabulky Disp_*). Zápis zakázek do Pohody řídí Pohoda:SyncMode (výchozí Live).";

    public const string MissingConnectionMessage =
        "Dispečink nemá nastavené ConnectionStrings:Dispatch. Účetnictví běží dál, jízdy se neukládají.";

    public const string UnavailableMessage =
        "Dispečink se nepodařilo načíst z SQL Serveru. Účetnictví běží dál.";
}

public interface IDispatchStore
{
    Task<DispatchSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task<DispatchMutation> AddTripAsync(NewTripRequest request, CancellationToken cancellationToken);

    Task<DispatchMutation> UpdateTripAsync(string id, UpdateTripRequest request, CancellationToken cancellationToken);

    Task<DispatchMutation> AssignAsync(string id, string? vehicleId, string? driverId, CancellationToken cancellationToken);

    Task<DispatchMutation> SetStatusAsync(string id, string? status, CancellationToken cancellationToken);

    Task<DispatchMutation> CancelTripAsync(string id, CancellationToken cancellationToken);

    Task<DispatchMutation> DeleteTripAsync(string id, int orderCount, CancellationToken cancellationToken);

    Task<DispatchRateCard> GetRatesAsync(CancellationToken cancellationToken);

    Task<string?> SaveRatesAsync(decimal defaultCostPerKm, IReadOnlyList<VehicleCostUpdate> vehicles, CancellationToken cancellationToken);
}
