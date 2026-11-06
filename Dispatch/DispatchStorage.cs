namespace PHMTRANS.Dispatch;

public static class DispatchStorage
{
    public const string Kind = "sql";

    public const string Note =
        "Vozidla, řidiči a zakázky jsou v SQL Serveru (tabulky Disp_*). Zápis zakázek do Pohody řídí Pohoda:SyncMode (výchozí Live).";

    public const string MissingConnectionMessage =
        "Dispečink nemá nastavené ConnectionStrings:Dispatch. Účetnictví běží dál, zakázky se neukládají.";

    public const string UnavailableMessage =
        "Dispečink se nepodařilo načíst z SQL Serveru. Účetnictví běží dál.";
}

public interface IDispatchStore
{
    Task<DispatchSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task<DispatchRateCard> GetRatesAsync(CancellationToken cancellationToken);

    Task<string?> SaveRatesAsync(decimal defaultCostPerKm, IReadOnlyList<VehicleCostUpdate> vehicles, CancellationToken cancellationToken);
}
