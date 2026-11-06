using System.Globalization;

namespace PHMTRANS.Dispatch;

// Čas nakládky a vykládky zakázky ("HH:mm"). Bez času se počítá s 6:00 až 14:00.
public static class OrderTimes
{
    public static readonly TimeOnly DefaultStart = new(6, 0);
    public static readonly TimeOnly DefaultEnd = new(14, 0);

    private static readonly string[] Formats = ["HH:mm", "H:mm", "HH:mm:ss"];

    public static (string? Value, string? Error) Parse(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);
        return TimeOnly.TryParseExact(value.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? (Format(time), null)
            : (null, $"{label} zadejte jako HH:MM.");
    }

    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static TimeOnly StartOf(string? value) =>
        TimeOnly.TryParseExact(value ?? "", Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : DefaultStart;

    public static TimeOnly EndOf(string? value) =>
        TimeOnly.TryParseExact(value ?? "", Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : DefaultEnd;

    // Začátek a konec zakázky v místním čase. Konec je vždy aspoň hodinu po začátku.
    public static (DateTime Start, DateTime End) Span(DispatchOrder order)
    {
        var start = order.LoadingDate.ToDateTime(StartOf(order.LoadingTime));
        var lastDay = order.UnloadingDate < order.LoadingDate ? order.LoadingDate : order.UnloadingDate;
        var end = lastDay.ToDateTime(EndOf(order.UnloadingTime));
        if (end <= start)
            end = start.AddHours(1);
        return (start, end);
    }
}
