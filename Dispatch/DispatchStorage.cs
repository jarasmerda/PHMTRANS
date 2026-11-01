namespace PHMTRANS.Dispatch;

public static class DispatchStorage
{
    public const string Kind = "sql";

    public const string Note =
        "Jízdy, vozidla a řidiči jsou v SQL Serveru (tabulky Disp_*). Do Pohody se nezapisuje.";

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
}
