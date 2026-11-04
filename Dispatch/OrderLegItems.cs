using System.Globalization;

namespace PHMTRANS.Dispatch;

// Prodejní položky pro Pohoda XML. Nákladová cena se do dokladu nedává.
public static class OrderLegItems
{
    public static IReadOnlyList<OrderXmlLine> SaleLines(DispatchOrder order)
    {
        var legs = order.Legs is { Count: > 0 }
            ? order.Legs.OrderBy(leg => leg.Sequence).ToArray()
            : Synthetic(order);
        return legs
            .Select(leg => new OrderXmlLine(Text(leg), 1m, leg.SalePrice))
            .ToArray();
    }

    public static string Text(OrderLeg leg) =>
        $"Leg {leg.Sequence.ToString(CultureInfo.InvariantCulture)}: {leg.FromPlace} → {leg.ToPlace}, {Liters(leg.Liters)} l";

    private static OrderLeg[] Synthetic(DispatchOrder order) =>
    [
        new(
            "legacy",
            1,
            order.LoadingPlace,
            order.UnloadingPlace,
            order.Liters,
            null,
            OrderPohodaFields.Money(order.AgreedPrice),
            true,
            0m)
    ];

    private static string Liters(decimal liters) => liters.ToString("0.###", CultureInfo.InvariantCulture);
}
