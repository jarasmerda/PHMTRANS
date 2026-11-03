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
        && left.LoadingPlace == right.LoadingPlace
        && left.UnloadingPlace == right.UnloadingPlace
        && left.LoadingDate == right.LoadingDate
        && left.UnloadingDate == right.UnloadingDate
        && left.FuelType == right.FuelType
        && left.Liters == right.Liters
        && left.AgreedPrice == right.AgreedPrice
        && left.Surcharges == right.Surcharges;

    public static bool SameDocument(DispatchOrder left, DispatchOrder right) =>
        Same(left, right)
        && left.Note == right.Note
        && left.CmrNumber == right.CmrNumber
        && left.VehicleId == right.VehicleId
        && left.DriverId == right.DriverId
        && left.TripId == right.TripId
        && left.Status == right.Status
        && left.IsLocked == right.IsLocked
        && left.PohodaInvoiceNumber == right.PohodaInvoiceNumber;
}
