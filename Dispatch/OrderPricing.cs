namespace PHMTRANS.Dispatch;

public static class OrderPricing
{
    public const string ByKm = "Km";

    public const string ByLiters = "Liters";

    public static bool IsKnown(string? mode) => mode is ByKm or ByLiters;

    public static decimal Rate(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    public static decimal Kilometers(decimal value) =>
        decimal.Round(value, 3, MidpointRounding.AwayFromZero);

    public static decimal Liters(decimal value) =>
        decimal.Round(value, 3, MidpointRounding.AwayFromZero);

    public static decimal Sale(decimal? kilometers, decimal liters, bool overridden, decimal overridePrice, string mode, decimal salePerKm, decimal salePerLiter)
    {
        if (overridden)
            return OrderPohodaFields.Money(overridePrice);

        if (mode == ByLiters)
            return OrderPohodaFields.Money(liters * salePerLiter);

        return OrderPohodaFields.Money((kilometers ?? 0m) * salePerKm);
    }

    public static decimal Cost(decimal? kilometers, decimal costPerKm) =>
        OrderPohodaFields.Money((kilometers ?? 0m) * costPerKm);

    public static OrderQuote Summarize(DispatchOrder order)
    {
        var legs = order.Legs ?? [];
        var stops = order.Stops ?? [];
        var totalKm = Kilometers(legs.Sum(leg => leg.Kilometers ?? 0m));
        var delivered = stops.Count > 0
            ? Liters(stops.Sum(stop => stop.Liters))
            : Liters(legs.Sum(leg => leg.Liters));
        var legSale = legs.Count > 0
            ? OrderPohodaFields.Money(legs.Sum(leg => leg.SalePrice))
            : OrderPohodaFields.Money(order.AgreedPrice);
        var totalCost = OrderPohodaFields.Money(legs.Sum(leg => leg.CostPrice));
        var totalSale = OrderPohodaFields.Money(legSale + order.Surcharges);
        var margin = OrderPohodaFields.Money(totalSale - totalCost);
        var marginPerKm = totalKm == 0 ? 0m : OrderPohodaFields.Money(margin / totalKm);
        var fuels = OrderLoads.Balance(order);
        var marginPercent = totalSale == 0
            ? 0m
            : decimal.Round(margin / totalSale * 100m, 2, MidpointRounding.AwayFromZero);
        return new OrderQuote(
            totalKm,
            order.Liters,
            delivered,
            totalCost,
            legSale,
            order.Surcharges,
            totalSale,
            margin,
            marginPerKm,
            marginPercent,
            delivered > order.Liters || fuels.Any(fuel => fuel.Delivered > fuel.Loaded),
            fuels);
    }
}

public sealed record OrderQuote(
    decimal TotalKm,
    decimal LoadedLiters,
    decimal DeliveredLiters,
    decimal TotalCost,
    decimal LegSale,
    decimal Surcharges,
    decimal TotalSale,
    decimal Margin,
    decimal MarginPerKm,
    decimal MarginPercent,
    bool DeliveredExceedsLoaded,
    IReadOnlyList<FuelBalance>? Fuels = null);
