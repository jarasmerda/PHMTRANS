using System.Globalization;

namespace PHMTRANS.Dispatch;

// Kalendář vytíženosti řidičů. Počítá se jen ze zakázek: řidič je obsazený
// od data nakládky do data vykládky. Stornované zakázky se nepočítají.
public static class DriverCalendar
{
    public const int DefaultDays = 14;
    public const int MaxDays = 62;
    public const string UnassignedName = "Bez řidiče";

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

    public static CalendarResponse Build(
        IReadOnlyList<DispatchDriver> drivers,
        IReadOnlyList<DispatchVehicle> vehicles,
        IReadOnlyList<DispatchOrder> orders,
        DateOnly from,
        int days)
    {
        var to = from.AddDays(days - 1);
        var dates = Enumerable.Range(0, days).Select(from.AddDays).ToArray();
        var plates = vehicles.ToDictionary(vehicle => vehicle.Id, vehicle => vehicle.Plate, StringComparer.Ordinal);
        var known = drivers.Select(driver => driver.Id).ToHashSet(StringComparer.Ordinal);
        var active = orders
            .Where(order => order.Status != OrderStatuses.Stornovano)
            .Where(order => Start(order) <= to && End(order) >= from)
            .OrderBy(order => order.LoadingDate)
            .ThenBy(order => order.Code, StringComparer.Ordinal)
            .ToArray();

        var rows = drivers
            .Select(driver => Row(driver.Id, driver.Name, driver.Phone, active.Where(order => order.DriverId == driver.Id), dates, plates))
            .ToList();
        var unassigned = active.Where(order => order.DriverId is null || !known.Contains(order.DriverId)).ToArray();
        if (unassigned.Length > 0)
            rows.Add(Row(null, UnassignedName, null, unassigned, dates, plates));

        return new CalendarResponse(from, to, dates, rows);
    }

    private static CalendarDriverRow Row(
        string? id,
        string name,
        string? phone,
        IEnumerable<DispatchOrder> orders,
        IReadOnlyList<DateOnly> dates,
        IReadOnlyDictionary<string, string> plates)
    {
        var list = orders.ToArray();
        var cells = dates
            .Select(date => new CalendarDay(
                date,
                list.Where(order => Start(order) <= date && End(order) >= date)
                    .Select(order => new CalendarOrder(
                        order.Id,
                        order.Code,
                        order.CustomerName,
                        order.Liters,
                        order.Status,
                        order.VehicleId is not null && plates.TryGetValue(order.VehicleId, out var plate) ? plate : null))
                    .ToArray()))
            .ToArray();
        var busy = cells.Count(cell => cell.Orders.Count > 0);
        var percent = dates.Count == 0 ? 0 : (int)Math.Round(busy * 100m / dates.Count, MidpointRounding.AwayFromZero);
        return new CalendarDriverRow(id, name, phone, busy, percent, cells);
    }

    // Hodinový kalendář: zakázky jako události s časem od–do (OrderTimes.Span).
    public static CalendarEvents Events(
        IReadOnlyList<DispatchDriver> drivers,
        IReadOnlyList<DispatchVehicle> vehicles,
        IReadOnlyList<DispatchOrder> orders,
        DateOnly from,
        int days)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = from.AddDays(days).ToDateTime(TimeOnly.MinValue);
        var plates = vehicles.ToDictionary(vehicle => vehicle.Id, vehicle => vehicle.Plate, StringComparer.Ordinal);
        var known = drivers.Select(driver => driver.Id).ToHashSet(StringComparer.Ordinal);
        var events = orders
            .Where(order => order.Status != OrderStatuses.Stornovano)
            .Select(order => (Order: order, Span: OrderTimes.Span(order)))
            .Where(item => item.Span.Start < end && item.Span.End > start)
            .OrderBy(item => item.Span.Start)
            .ThenBy(item => item.Order.Code, StringComparer.Ordinal)
            .Select(item => new CalendarEvent(
                item.Order.Id,
                item.Order.Code,
                item.Order.CustomerName,
                item.Order.LoadingPlace,
                item.Order.UnloadingPlace,
                item.Order.Liters,
                item.Order.Status,
                item.Order.DriverId is not null && known.Contains(item.Order.DriverId) ? item.Order.DriverId : null,
                item.Order.VehicleId is not null && plates.TryGetValue(item.Order.VehicleId, out var plate) ? plate : null,
                item.Span.Start,
                item.Span.End,
                item.Order.LoadingTime is null || item.Order.UnloadingTime is null))
            .ToArray();
        return new CalendarEvents(
            from,
            from.AddDays(days - 1),
            drivers.Select(driver => new CalendarDriver(driver.Id, driver.Name, driver.Phone)).ToArray(),
            events);
    }

    private static DateOnly Start(DispatchOrder order) => order.LoadingDate;

    private static DateOnly End(DispatchOrder order) =>
        order.UnloadingDate < order.LoadingDate ? order.LoadingDate : order.UnloadingDate;
}

public sealed record CalendarOrder(string Id, string Code, string Customer, decimal Liters, string Status, string? Vehicle);

public sealed record CalendarDay(DateOnly Date, IReadOnlyList<CalendarOrder> Orders);

public sealed record CalendarDriverRow(
    string? DriverId,
    string Name,
    string? Phone,
    int BusyDays,
    int Percent,
    IReadOnlyList<CalendarDay> Days);

public sealed record CalendarDriver(string Id, string Name, string? Phone);

// Start a End jsou místní čas bez zóny ("2026-11-06T06:00:00"). Estimated = bez zadaného času, bere se 6:00–14:00.
public sealed record CalendarEvent(
    string Id,
    string Code,
    string Customer,
    string LoadingPlace,
    string UnloadingPlace,
    decimal Liters,
    string Status,
    string? DriverId,
    string? Vehicle,
    DateTime Start,
    DateTime End,
    bool Estimated);

public sealed record CalendarEvents(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CalendarDriver> Drivers,
    IReadOnlyList<CalendarEvent> Events);

public sealed record CalendarResponse(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<CalendarDriverRow> Drivers);
