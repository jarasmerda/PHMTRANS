namespace PHMTRANS.Dispatch;

public static class OrderPohodaFields
{
    public static decimal Total(DispatchOrder order) =>
        Money(order.AgreedPrice) + Money(order.Surcharges);

    public static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static bool Same(DispatchOrder left, DispatchOrder right) =>
        left.CustomerPohodaId == right.CustomerPohodaId
        && left.CustomerName == right.CustomerName
        && left.CustomerIco == right.CustomerIco
        && left.CustomerDic == right.CustomerDic
        && left.CustomerStreet == right.CustomerStreet
        && left.CustomerCity == right.CustomerCity
        && left.CustomerZip == right.CustomerZip
        && left.CustomerEmail == right.CustomerEmail
        && left.CustomerPhone == right.CustomerPhone
        && left.LoadingPlace == right.LoadingPlace
        && left.UnloadingPlace == right.UnloadingPlace
        && left.LoadingDate == right.LoadingDate
        && left.UnloadingDate == right.UnloadingDate
        && left.FuelType == right.FuelType
        && left.FuelTypeId == right.FuelTypeId
        && left.Liters == right.Liters
        && left.AgreedPrice == right.AgreedPrice
        && left.Surcharges == right.Surcharges
        && SameSaleRoute(left, right);

    public static bool SameDocument(DispatchOrder left, DispatchOrder right) =>
        Same(left, right)
        && left.Note == right.Note
        && left.CmrNumber == right.CmrNumber
        && left.VehicleId == right.VehicleId
        && left.DriverId == right.DriverId
        && left.TripId == right.TripId
        && left.Status == right.Status
        && left.IsLocked == right.IsLocked
        && left.PohodaInvoiceNumber == right.PohodaInvoiceNumber
        && SameCostRoute(left, right)
        && OrderLoads.SameLoads(left, right);

    // Místa a prodejní cena úseků jdou do textu a částky položky v Pohodě.
    // Náklad, km při ruční prodejní ceně, vozidlo, řidič a jízda v XML nejsou.
    private static bool SameSaleRoute(DispatchOrder left, DispatchOrder right)
    {
        var leftStops = left.Stops ?? [];
        var rightStops = right.Stops ?? [];
        if (leftStops.Count != rightStops.Count)
            return false;
        for (var index = 0; index < leftStops.Count; index++)
        {
            if (leftStops[index].Sequence != rightStops[index].Sequence
                || leftStops[index].Place != rightStops[index].Place
                || leftStops[index].Liters != rightStops[index].Liters
                || leftStops[index].FuelTypeId != rightStops[index].FuelTypeId
                || leftStops[index].FuelType != rightStops[index].FuelType)
                return false;
        }

        var leftLegs = left.Legs ?? [];
        var rightLegs = right.Legs ?? [];
        if (leftLegs.Count != rightLegs.Count)
            return false;
        for (var index = 0; index < leftLegs.Count; index++)
        {
            if (leftLegs[index].Sequence != rightLegs[index].Sequence
                || leftLegs[index].FromPlace != rightLegs[index].FromPlace
                || leftLegs[index].ToPlace != rightLegs[index].ToPlace
                || leftLegs[index].Liters != rightLegs[index].Liters
                || leftLegs[index].SalePrice != rightLegs[index].SalePrice)
                return false;
        }

        return true;
    }

    private static bool SameCostRoute(DispatchOrder left, DispatchOrder right)
    {
        if (left.PricingMode != right.PricingMode
            || left.SalePerKm != right.SalePerKm
            || left.SalePerLiter != right.SalePerLiter
            || left.CostPerKm != right.CostPerKm)
            return false;

        var leftStops = left.Stops ?? [];
        var rightStops = right.Stops ?? [];
        if (leftStops.Count != rightStops.Count)
            return false;
        for (var index = 0; index < leftStops.Count; index++)
        {
            if (leftStops[index].Note != rightStops[index].Note
                || leftStops[index].ArrivalAt != rightStops[index].ArrivalAt)
                return false;
        }

        var leftLegs = left.Legs ?? [];
        var rightLegs = right.Legs ?? [];
        if (leftLegs.Count != rightLegs.Count)
            return false;
        for (var index = 0; index < leftLegs.Count; index++)
        {
            if (leftLegs[index].Kilometers != rightLegs[index].Kilometers
                || leftLegs[index].BilledKilometers != rightLegs[index].BilledKilometers
                || leftLegs[index].SaleOverridden != rightLegs[index].SaleOverridden
                || leftLegs[index].CostPrice != rightLegs[index].CostPrice)
                return false;
        }

        return true;
    }
}
