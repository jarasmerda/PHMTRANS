namespace PHMTRANS.Dispatch;

// Nakládací místo (sklad, rafinerie) z dbo.Disp_LoadingPlace.
public sealed record LoadingPlaceItem(int Id, string Name);

// Bod trasy z dbo.Disp_Point: nakládka, vykládka, nebo obojí.
public sealed record PointItem(int Id, string Name, bool IsLoading, bool IsUnloading);

public sealed record FuelTypeItem(int Id, string Name);

public static class FuelTypeCatalog
{
    public const string Diesel = "Motorová nafta";
    public const string Petrol = "Benzín";

    public static readonly IReadOnlyList<FuelTypeItem> Seed =
    [
        new(1, Diesel),
        new(2, Petrol)
    ];

    public static OrderCatalog WithSeed(OrderCatalog catalog) =>
        catalog.FuelTypes is { Count: > 0 } ? catalog : catalog with { FuelTypes = Seed };

    public static FuelTypeItem? Find(IReadOnlyList<FuelTypeItem>? types, int? id, string? name)
    {
        if (types is not { Count: > 0 })
            return null;

        if (id is int value)
        {
            var byId = types.FirstOrDefault(item => item.Id == value);
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        return types.FirstOrDefault(item => item.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
