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
        var stops = order.Stops ?? [];
        return legs
            .Select(leg =>
            {
                var stop = stops.FirstOrDefault(item => item.Sequence == leg.Sequence);
                var stopFuel = stop is null ? "" : OrderStopItems.FuelText(stop);
                var fuel = string.IsNullOrWhiteSpace(stopFuel) ? order.FuelType : stopFuel;
                return Line(order, leg, Text(leg, fuel));
            })
            .ToArray();
    }

    // Do Pohody jde množství a cena za jednotku: fakturované km × Kč/km, nebo litry × Kč/l.
    // Ruční cena úseku, úsek bez km/litrů nebo cena, která na množství × sazbu nesedí na haléř,
    // jde jako 1 ks za celou cenu, aby součet v Pohodě vždy seděl.
    public static OrderXmlLine Line(DispatchOrder order, OrderLeg leg, string text)
    {
        if (!leg.SaleOverridden)
        {
            var unit = order.PricingMode switch
            {
                OrderPricing.ByLiters when leg.Liters > 0m => new OrderXmlLine(text, leg.Liters, order.SalePerLiter, "l"),
                OrderPricing.ByKm when (leg.BilledKilometers ?? leg.Kilometers) is > 0m and var km =>
                    new OrderXmlLine(text, km, order.SalePerKm, "km"),
                _ => null
            };
            if (unit is not null && unit.Amount == leg.SalePrice)
                return unit;
        }

        return new OrderXmlLine(text, 1m, leg.SalePrice);
    }

    public static string Text(OrderLeg leg, string? fuel = null)
    {
        var fuelPart = string.IsNullOrWhiteSpace(fuel) ? "" : $", {fuel.Trim()}";
        return $"Leg {leg.Sequence.ToString(CultureInfo.InvariantCulture)}: {leg.FromPlace} → {leg.ToPlace}{fuelPart}, {Liters(leg.Liters)} l";
    }

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
