namespace PHMTRANS.Dispatch;

public static class OrderStatuses
{
    public const string Nova = "Nová";
    public const string Naplanovana = "Naplánovaná";
    public const string NaCeste = "Na cestě";
    public const string Doruceno = "Doručeno";
    public const string KFakturaci = "K fakturaci";
    public const string Vyfakturovano = "Vyfakturováno";
    public const string Stornovano = "Stornováno";

    public const string UnknownMessage =
        "Neznámý stav. Povolené jsou Nová, Naplánovaná, Na cestě, Doručeno, K fakturaci, Vyfakturováno a Stornováno.";

    public static readonly IReadOnlyList<string> All =
        [Nova, Naplanovana, NaCeste, Doruceno, KFakturaci, Vyfakturovano, Stornovano];

    public static readonly IReadOnlyList<string> Editable =
        [Nova, Naplanovana, NaCeste, Doruceno, KFakturaci, Vyfakturovano];

    public static bool IsKnown(string status) => All.Contains(status);

    public static bool IsConfirmed(string status) => status is KFakturaci or Vyfakturovano;

    public static int Rank(string status) => status switch
    {
        Nova => 0,
        Naplanovana => 1,
        NaCeste => 2,
        Doruceno => 3,
        KFakturaci => 4,
        Vyfakturovano => 5,
        Stornovano => 6,
        _ => 7
    };

    public static bool CountsInTotals(string status) => status != Stornovano;
}

public static class OrderSyncActions
{
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Correction = "Correction";
    public const string Delete = "Delete";
    public const string Cancel = "Cancel";

    public static bool CountsTowardCorrectionIndex(string action) =>
        action is Correction or Cancel;
}

public static class OrderQueueStatuses
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string DryRun = "DryRun";

    public static bool IsOpen(string status) => status is Pending or DryRun;
}

public static class OrderSyncLimits
{
    public const int MaxAttempts = 6;

    public const int LastErrorMaxLength = 1000;

    public static bool IsDue(DateTimeOffset? processedUtc, int attempts, DateTimeOffset now)
    {
        if (processedUtc is null || attempts <= 0)
            return true;

        var seconds = Math.Min(900d, 15d * Math.Pow(2d, attempts - 1));
        return processedUtc.Value.AddSeconds(seconds) <= now;
    }

    public static string? TrimToColumn(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Cut(value.Trim(), max);
    }

    public static string Cut(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || max <= 0)
            return "";

        if (value.Length <= max)
            return value;

        if (char.IsHighSurrogate(value[max - 1]))
            max--;

        return value[..max];
    }
}

public sealed record DispatchOrder(
    string Id,
    string Code,
    int? CustomerPohodaId,
    string CustomerName,
    string LoadingPlace,
    string UnloadingPlace,
    DateOnly LoadingDate,
    DateOnly UnloadingDate,
    string FuelType,
    decimal Liters,
    decimal AgreedPrice,
    decimal Surcharges,
    string? Note,
    string? CmrNumber,
    string? VehicleId,
    string? DriverId,
    string? TripId,
    string Status,
    bool IsLocked,
    int CurrentVersion,
    int? PohodaOrderId,
    string? PohodaOrderNumber,
    string? PohodaInvoiceNumber,
    int? LastSentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LatestSyncStatus = null,
    string? LatestSyncError = null,
    string? LatestSyncWarning = null,
    string PricingMode = OrderPricing.ByKm,
    decimal SalePerKm = 0m,
    decimal SalePerLiter = 0m,
    decimal CostPerKm = 0m,
    IReadOnlyList<OrderStop>? Stops = null,
    IReadOnlyList<OrderLeg>? Legs = null,
    OrderQuote? Quote = null,
    string? CancelReason = null,
    string? CustomerIco = null,
    string? CustomerDic = null,
    string? CustomerStreet = null,
    string? CustomerCity = null,
    string? CustomerZip = null,
    string? CustomerEmail = null,
    string? CustomerPhone = null);

public sealed record OrderSaveRequest(
    int? CustomerPohodaId,
    string? CustomerName,
    string? LoadingPlace,
    string? UnloadingPlace,
    DateOnly? LoadingDate,
    DateOnly? UnloadingDate,
    string? FuelType,
    decimal? Liters,
    decimal? AgreedPrice,
    decimal? Surcharges,
    string? Note,
    string? CmrNumber,
    string? VehicleId,
    string? DriverId,
    string? TripId,
    string? Status,
    string? PohodaInvoiceNumber,
    string? PricingMode = null,
    decimal? SalePerKm = null,
    decimal? SalePerLiter = null,
    decimal? CostPerKm = null,
    IReadOnlyList<OrderStopInput>? Stops = null,
    IReadOnlyList<OrderLegInput>? Legs = null,
    string? CustomerIco = null,
    string? CustomerDic = null,
    string? CustomerStreet = null,
    string? CustomerCity = null,
    string? CustomerZip = null,
    string? CustomerEmail = null,
    string? CustomerPhone = null);

public sealed record OrderSyncEntry(
    long Id,
    string OrderId,
    string OrderCode,
    int? VersionFrom,
    int VersionTo,
    string Action,
    string PayloadXml,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ProcessedUtc,
    string? PohodaDocNumber,
    string? Warning = null);

public sealed record OrderStatusCount(string Status, int Count);

public sealed record OrderListResponse(
    string SyncMode,
    string SyncNote,
    IReadOnlyList<OrderStatusCount> Counts,
    IReadOnlyList<DispatchOrder> Orders);

public sealed record OrderDetailResponse(
    DispatchOrder Order,
    IReadOnlyList<DispatchOrder> Versions,
    IReadOnlyList<OrderSyncEntry> Sync,
    string SyncMode,
    string SyncNote);

public sealed record OrderBundle(
    DispatchOrder Order,
    IReadOnlyList<DispatchOrder> Versions,
    IReadOnlyList<OrderSyncEntry> Sync);

public sealed record PohodaImportResult(bool Ok, int? Id, string? Number, string? Error, bool DeleteCommitted = false)
{
    public static PohodaImportResult Success(int? id, string? number) => new(true, id, number, null);

    public static PohodaImportResult Failure(string error, bool deleteCommitted = false) =>
        new(false, null, null, error, deleteCommitted);
}

public abstract record OrderMutation
{
    private OrderMutation()
    {
    }

    public sealed record Success(DispatchOrder Order) : OrderMutation;

    public sealed record Deleted(string Id) : OrderMutation;

    public sealed record NotFound : OrderMutation;

    public sealed record Rejected(string Message) : OrderMutation;
}

public sealed record CancelOrderRequest(string? Reason, bool Delete = false);

public sealed class OrderStoreException(string message, Exception? inner = null) : Exception(message, inner);
