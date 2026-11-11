using System.Globalization;

namespace PHMTRANS.Dispatch;

// Kalendář vytíženosti vozidel. Počítá se jen ze zakázek: vozidlo je obsazené
// od data nakládky do data vykládky. Stornované zakázky se nepočítají. Řidiči se nepoužívají.
public static class VehicleCalendar
{
    public const int DefaultDays = 14;
    public const int MaxDays = 62;
    public const string UnassignedName = "Bez vozidla";

    public static (DateOnly From, int Days, string? Error) ParseRange(string? from, int? days, DateOnly today)
    {
        var count = days ?? DefaultDays;
        if (count is < 1 or > MaxDays)
            return (today, 0, $"Kalendář umí 1 až {MaxDays} dní.");
        if (string.IsNullOrWhiteSpace(from))
            return (WeekStart(today), count, null);
        return DateOnly.TryParseExact(from.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? (start, count, null)
            : (today, 0, "Datum začátku zadejte jako RRRR-MM-DD.");
    }

    public static DateOnly WeekStart(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static string Name(DispatchVehicle vehicle) =>
        string.IsNullOrWhiteSpace(vehicle.Label) ? vehicle.Plate : $"{vehicle.Plate} · {vehicle.Label}";

    public static CalendarResponse Build(
        IReadOnlyList<DispatchVehicle> vehicles,
        IReadOnlyList<DispatchOrder> orders,
        DateOnly from,
        int days)
    {
        var to = from.AddDays(days - 1);
        var dates = Enumerable.Range(0, days).Select(from.AddDays).ToArray();
        var known = vehicles.Select(vehicle => vehicle.Id).ToHashSet(StringComparer.Ordinal);
        var active = orders
            .Where(order => order.Status != OrderStatuses.Stornovano)
            .Where(order => Start(order) <= to && End(order) >= from)
            .OrderBy(order => order.LoadingDate)
            .ThenBy(order => order.Code, StringComparer.Ordinal)
            .ToArray();

        var rows = vehicles
            .Select(vehicle => Row(vehicle.Id, Name(vehicle), active.Where(order => order.VehicleId == vehicle.Id), dates))
            .ToList();
        var unassigned = active.Where(order => order.VehicleId is null || !known.Contains(order.VehicleId)).ToArray();
        if (unassigned.Length > 0)
            rows.Add(Row(null, UnassignedName, unassigned, dates));

        return new CalendarResponse(from, to, dates, rows);
    }

    private static CalendarVehicleRow Row(
        string? id,
        string name,
        IEnumerable<DispatchOrder> orders,
        IReadOnlyList<DateOnly> dates)
    {
        var list = orders.ToArray();
        var cells = dates
            .Select(date => new CalendarDay(
                date,
                list.Where(order => Start(order) <= date && End(order) >= date)
                    .Select(order => new CalendarOrder(order.Id, order.Code, order.CustomerName, order.Liters, order.Status))
                    .ToArray()))
            .ToArray();
        var busy = cells.Count(cell => cell.Orders.Count > 0);
        var percent = dates.Count == 0 ? 0 : (int)Math.Round(busy * 100m / dates.Count, MidpointRounding.AwayFromZero);
        return new CalendarVehicleRow(id, name, busy, percent, cells);
    }

    // Hodinový kalendář: zakázky jako události s časem od–do (OrderTimes.Span).
    public static CalendarEvents Events(
        IReadOnlyList<DispatchVehicle> vehicles,
        IReadOnlyList<DispatchOrder> orders,
        DateOnly from,
        int days)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = from.AddDays(days).ToDateTime(TimeOnly.MinValue);
        var plates = vehicles.ToDictionary(vehicle => vehicle.Id, vehicle => vehicle.Plate, StringComparer.Ordinal);
        var events = orders
            .Where(order => order.Status != OrderStatuses.Stornovano)
            .Select(order => (Order: order, Span: OrderTimes.Span(order)))
            .Where(item => item.Span.Start < end && item.Span.End > start)
            .OrderBy(item => item.Span.Start)
            .ThenBy(item => item.Order.Code, StringComparer.Ordinal)
            .Select(item =>
            {
                var known = item.Order.VehicleId is not null && plates.ContainsKey(item.Order.VehicleId);
                return new CalendarEvent(
                    item.Order.Id,
                    item.Order.Code,
                    item.Order.CustomerName,
                    item.Order.LoadingPlace,
                    item.Order.UnloadingPlace,
                    item.Order.Liters,
                    item.Order.Status,
                    known ? item.Order.VehicleId : null,
                    known ? plates[item.Order.VehicleId!] : null,
                    item.Span.Start,
                    item.Span.End,
                    item.Order.LoadingTime is null || item.Order.UnloadingTime is null);
            })
            .ToArray();
        return new CalendarEvents(
            from,
            from.AddDays(days - 1),
            vehicles.Select(vehicle => new CalendarVehicle(vehicle.Id, vehicle.Plate, vehicle.Label)).ToArray(),
            events);
    }

    private static DateOnly Start(DispatchOrder order) => order.LoadingDate;

    private static DateOnly End(DispatchOrder order) =>
        order.UnloadingDate < order.LoadingDate ? order.LoadingDate : order.UnloadingDate;
}

public sealed record CalendarOrder(string Id, string Code, string Customer, decimal Liters, string Status);

public sealed record CalendarDay(DateOnly Date, IReadOnlyList<CalendarOrder> Orders);

public sealed record CalendarVehicleRow(
    string? VehicleId,
    string Name,
    int BusyDays,
    int Percent,
    IReadOnlyList<CalendarDay> Days);

// Vozidlo v hodinovém kalendáři (filtr, barva, řádek plánování).
public sealed record CalendarVehicle(string Id, string Plate, string Label);

// Start a End jsou místní čas bez zóny ("2026-11-06T06:00:00"). Estimated = bez zadaného času, bere se 6:00–14:00.
public sealed record CalendarEvent(
    string Id,
    string Code,
    string Customer,
    string LoadingPlace,
    string UnloadingPlace,
    decimal Liters,
    string Status,
    string? VehicleId,
    string? Vehicle,
    DateTime Start,
    DateTime End,
    bool Estimated);

public sealed record CalendarEvents(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CalendarVehicle> Vehicles,
    IReadOnlyList<CalendarEvent> Events);

public sealed record CalendarResponse(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<CalendarVehicleRow> Vehicles);
