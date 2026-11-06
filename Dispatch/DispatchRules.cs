namespace PHMTRANS.Dispatch;

public static class DispatchRules
{
    public static DispatchSnapshot Snapshot(DispatchDocument document) =>
        new(document.Vehicles.ToArray(), document.Drivers.ToArray(), document.DefaultCostPerKm);

    public static DispatchRateCard Rates(DispatchDocument document) =>
        new(document.DefaultCostPerKm, document.Vehicles.ToArray());

    public static string? SaveRates(DispatchDocument document, decimal defaultCostPerKm, IReadOnlyList<VehicleCostUpdate> vehicles)
    {
        if (defaultCostPerKm is < 0m or > 1_000_000m)
            return "Nákladová sazba je mimo povolený rozsah.";

        var updates = new List<(int Index, decimal? Cost)>();
        foreach (var vehicle in vehicles)
        {
            if (string.IsNullOrWhiteSpace(vehicle.Id))
                return "Neznámé vozidlo.";
            var index = document.Vehicles.FindIndex(item => item.Id == vehicle.Id);
            if (index < 0)
                return "Neznámé vozidlo.";
            if (vehicle.CostPerKm is < 0m or > 1_000_000m)
                return "Nákladová sazba je mimo povolený rozsah.";
            updates.Add((index, vehicle.CostPerKm is null ? null : OrderPricing.Rate(vehicle.CostPerKm.Value)));
        }

        document.DefaultCostPerKm = OrderPricing.Rate(defaultCostPerKm);
        foreach (var update in updates)
        {
            var current = document.Vehicles[update.Index];
            document.Vehicles[update.Index] = current with { CostPerKm = update.Cost };
        }

        return null;
    }
}
