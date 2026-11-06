using Microsoft.Extensions.Options;
using PHMTRANS.Dispatch;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderRouteTests
{
    [Fact]
    public void Legs_follow_the_loading_and_each_next_stop()
    {
        var stops = new[]
        {
            new OrderStopInput("ČS Brno", 8000m, null, null),
            new OrderStopInput("ČS Olomouc", 10000m, "volat", null),
            new OrderStopInput("ČS Plzeň", 12000m, null, null)
        };
        var legs = new[]
        {
            new OrderLegInput(180m, null, false),
            new OrderLegInput(140m, null, false),
            new OrderLegInput(160m, 9000m, true)
        };
        var request = Request(stops, legs, OrderPricing.ByKm, salePerKm: 46m, costPerKm: 28m);
        var built = Blank();
        var routed = OrderRoute.Materialize(built, request, OrderCatalog.Empty).Order!;

        Assert.Equal(3, routed.Legs!.Count);
        Assert.Equal(("Sklad Kralupy", "ČS Brno"), (routed.Legs[0].FromPlace, routed.Legs[0].ToPlace));
        Assert.Equal(("ČS Brno", "ČS Olomouc"), (routed.Legs[1].FromPlace, routed.Legs[1].ToPlace));
        Assert.Equal(("ČS Olomouc", "ČS Plzeň"), (routed.Legs[2].FromPlace, routed.Legs[2].ToPlace));
        Assert.Equal("ČS Plzeň", routed.UnloadingPlace);
        Assert.Equal(8000m, routed.Legs[0].Liters);
        Assert.Equal(8280m, routed.Legs[0].SalePrice);
        Assert.Equal(5040m, routed.Legs[0].CostPrice);
        Assert.Equal(6440m, routed.Legs[1].SalePrice);
        Assert.Equal(9000m, routed.Legs[2].SalePrice);
        Assert.True(routed.Legs[2].SaleOverridden);
        Assert.Equal(4480m, routed.Legs[2].CostPrice);
        Assert.Equal(23720m, routed.AgreedPrice);
        Assert.Equal(23720m, routed.Quote!.LegSale);
        Assert.False(routed.Quote.DeliveredExceedsLoaded);
    }

    [Fact]
    public void Kilometer_mode_prices_sale_from_km_and_cost_from_the_same_distance()
    {
        var quote = Quote(
            OrderPricing.ByKm,
            salePerKm: 10m,
            salePerLiter: 99m,
            costPerKm: 4m,
            surcharges: 100m,
            loaded: 20000m,
            legs:
            [
                Leg(100m, 10000m, false, 0m),
                Leg(50m, 5000m, false, 0m)
            ]);

        Assert.Equal(150m, quote.TotalKm);
        Assert.Equal(15000m, quote.DeliveredLiters);
        Assert.Equal(1500m, quote.LegSale);
        Assert.Equal(600m, quote.TotalCost);
        Assert.Equal(1600m, quote.TotalSale);
        Assert.Equal(1000m, quote.Margin);
        Assert.Equal(6.67m, quote.MarginPerKm);
        Assert.Equal(62.5m, quote.MarginPercent);
        Assert.False(quote.DeliveredExceedsLoaded);
    }

    [Fact]
    public void Liter_mode_prices_sale_from_delivered_liters_and_keeps_cost_per_km()
    {
        var quote = Quote(
            OrderPricing.ByLiters,
            salePerKm: 10m,
            salePerLiter: 0.5m,
            costPerKm: 4m,
            surcharges: 0m,
            loaded: 20000m,
            legs:
            [
                Leg(100m, 10000m, false, 0m),
                Leg(50m, 5000m, false, 0m)
            ]);

        Assert.Equal(7500m, quote.LegSale);
        Assert.Equal(600m, quote.TotalCost);
        Assert.Equal(7500m, quote.TotalSale);
        Assert.Equal(6900m, quote.Margin);
        Assert.Equal(46m, quote.MarginPerKm);
        Assert.Equal(92m, quote.MarginPercent);
    }

    [Fact]
    public void Manual_leg_price_replaces_the_calculated_sale_and_leaves_cost()
    {
        var quote = Quote(
            OrderPricing.ByKm,
            salePerKm: 10m,
            salePerLiter: 0.5m,
            costPerKm: 4m,
            surcharges: 0m,
            loaded: 12000m,
            legs:
            [
                Leg(100m, 10000m, true, 800m),
                Leg(50m, 5000m, false, 0m)
            ]);

        Assert.Equal(1300m, quote.LegSale);
        Assert.Equal(600m, quote.TotalCost);
        Assert.Equal(700m, quote.Margin);
        Assert.Equal(4.67m, quote.MarginPerKm);
        Assert.Equal(53.85m, quote.MarginPercent);
        Assert.True(quote.DeliveredExceedsLoaded);
    }

    [Fact]
    public void Zero_distance_and_zero_sale_do_not_divide()
    {
        var quote = Quote(
            OrderPricing.ByKm,
            salePerKm: 10m,
            salePerLiter: 1m,
            costPerKm: 4m,
            surcharges: 0m,
            loaded: 1000m,
            legs: [Leg(0m, 0m, true, 0m)]);

        Assert.Equal(0m, quote.TotalKm);
        Assert.Equal(0m, quote.TotalSale);
        Assert.Equal(0m, quote.MarginPerKm);
        Assert.Equal(0m, quote.MarginPercent);
    }

    [Fact]
    public void Legacy_order_becomes_one_stop_and_one_overridden_leg()
    {
        var route = OrderRoute.Legacy("Sklad Kralupy", "ČS Brno", 32000m, 45000m);

        var stop = Assert.Single(route.Stops);
        Assert.Equal(1, stop.Sequence);
        Assert.Equal("ČS Brno", stop.Place);
        Assert.Equal(32000m, stop.Liters);
        var leg = Assert.Single(route.Legs);
        Assert.Equal("Sklad Kralupy", leg.FromPlace);
        Assert.Equal("ČS Brno", leg.ToPlace);
        Assert.Equal(45000m, leg.SalePrice);
        Assert.True(leg.SaleOverridden);
        Assert.Equal(0m, leg.CostPrice);
    }

    [Fact]
    public void Sale_lines_use_leg_prices_and_skip_cost()
    {
        var order = Blank() with
        {
            Legs =
            [
                new OrderLeg("a", 1, "Sklad", "ČS Brno", 8000m, 10m, 1500m, false, 400m),
                new OrderLeg("b", 2, "ČS Brno", "ČS Olomouc", 4000m, 20m, 900m, true, 800m)
            ]
        };

        var lines = OrderLegItems.SaleLines(order);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Leg 1: Sklad → ČS Brno, motorová nafta, 8000 l", lines[0].Text);
        Assert.Equal(1500m, lines[0].UnitPrice);
        Assert.Equal("Leg 2: ČS Brno → ČS Olomouc, motorová nafta, 4000 l", lines[1].Text);
        Assert.Equal(900m, lines[1].UnitPrice);
        Assert.DoesNotContain(lines, line => line.UnitPrice == 400m || line.UnitPrice == 800m);
    }

    [Fact]
    public void Stop_change_is_pohoda_relevant_and_cost_or_trip_is_not()
    {
        var sent = Priced(OrderPricing.ByKm, 10m, 0m, 4m, 100m, 5000m, false);
        var moved = sent with { TripId = "trip-2", VehicleId = "veh-1", DriverId = "drv-1", CurrentVersion = 3 };
        Assert.Equal(OrderSyncAction.None, OrderSyncPlanner.Plan(moved, sent, sent, confirmRequested: false).Action);

        var costlier = sent with
        {
            CurrentVersion = 3,
            CostPerKm = 9m,
            Legs = sent.Legs!.Select(leg => leg with { CostPrice = 900m }).ToArray()
        };
        Assert.Equal(OrderSyncAction.None, OrderSyncPlanner.Plan(costlier, sent, sent, confirmRequested: false).Action);
        Assert.False(OrderPohodaFields.SameDocument(sent, costlier));

        var rerouted = sent with
        {
            CurrentVersion = 3,
            UnloadingPlace = "ČS Liberec",
            AgreedPrice = 2000m,
            Stops = [sent.Stops![0] with { Place = "ČS Liberec" }],
            Legs = [sent.Legs![0] with { ToPlace = "ČS Liberec", SalePrice = 2000m }]
        };
        Assert.Equal(OrderSyncAction.Update, OrderSyncPlanner.Plan(rerouted, sent, sent, confirmRequested: false).Action);
    }

    private static OrderQuote Quote(
        string mode,
        decimal salePerKm,
        decimal salePerLiter,
        decimal costPerKm,
        decimal surcharges,
        decimal loaded,
        OrderLegInput[] legs)
    {
        var stops = legs.Select((leg, index) => new OrderStopInput($"ČS {index + 1}", leg.Kilometers ?? 0m, null, null)).ToArray();
        // Liters are passed through the stop, not through kilometers. Rebuild stops from a parallel convention:
        stops = Enumerable.Range(0, legs.Length)
            .Select(index => new OrderStopInput($"ČS {index + 1}", Delivered(index), null, null))
            .ToArray();
        var request = Request(stops, legs, mode, salePerKm, salePerLiter, costPerKm, surcharges, loaded);
        var order = OrderRoute.Materialize(Blank() with { Liters = loaded, Surcharges = surcharges }, request, OrderCatalog.Empty).Order!;
        return order.Quote!;

        decimal Delivered(int index) => index switch
        {
            0 => 10000m,
            1 => 5000m,
            _ => 0m
        };
    }

    private static OrderLegInput Leg(decimal km, decimal liters, bool overridden, decimal price) =>
        new(km, price, overridden);

    private static DispatchOrder Priced(
        string mode,
        decimal salePerKm,
        decimal salePerLiter,
        decimal costPerKm,
        decimal km,
        decimal liters,
        bool overridden)
    {
        var request = Request(
            [new OrderStopInput("ČS Brno", liters, null, null)],
            [new OrderLegInput(km, null, overridden)],
            mode,
            salePerKm,
            salePerLiter,
            costPerKm,
            0m,
            liters);
        var order = OrderRoute.Materialize(
            Blank() with { Liters = liters, Status = OrderStatuses.KFakturaci, PohodaOrderId = 15, LastSentVersion = 2, CurrentVersion = 2 },
            request,
            OrderCatalog.Empty).Order!;
        return order with { CurrentVersion = 2 };
    }

    private static DispatchOrder Blank() => new(
        "ord-1",
        "Z2026-0001",
        null,
        "Zákazník",
        "Sklad Kralupy",
        "původní",
        new DateOnly(2026, 11, 4),
        new DateOnly(2026, 11, 4),
        "motorová nafta",
        30000m,
        0m,
        0m,
        null,
        null,
        null,
        null,
        null,
        OrderStatuses.Nova,
        false,
        1,
        null,
        null,
        null,
        null,
        DateTimeOffset.Parse("2026-11-04T06:00:00+01:00"),
        DateTimeOffset.Parse("2026-11-04T06:00:00+01:00"));

    private static OrderSaveRequest Request(
        IReadOnlyList<OrderStopInput> stops,
        IReadOnlyList<OrderLegInput> legs,
        string mode,
        decimal salePerKm = 0m,
        decimal salePerLiter = 0m,
        decimal costPerKm = 0m,
        decimal surcharges = 0m,
        decimal liters = 30000m) =>
        new(
            42,
            "Zákazník",
            "Sklad Kralupy",
            null,
            new DateOnly(2026, 11, 4),
            new DateOnly(2026, 11, 4),
            "motorová nafta",
            liters,
            null,
            surcharges,
            null,
            null,
            null,
            null,
            null,
            OrderStatuses.Nova,
            null,
            mode,
            salePerKm,
            salePerLiter,
            costPerKm,
            stops,
            legs);
}

public sealed class OrderAssignmentTests
{
    [Fact]
    public async Task One_trip_keeps_many_orders_and_a_taken_order_cannot_be_added()
    {
        var dispatch = new MemoryDispatchStore();
        var orders = new MemoryOrderStore(dispatch, Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        var first = Assert.IsType<OrderMutation.Success>(await orders.CreateAsync(Plain(), CancellationToken.None));
        var second = Assert.IsType<OrderMutation.Success>(await orders.CreateAsync(Plain(), CancellationToken.None));
        var third = Assert.IsType<OrderMutation.Success>(await orders.CreateAsync(Plain(), CancellationToken.None));

        Assert.Null(await orders.AttachOrdersAsync("trip-2", [first.Order.Id, second.Order.Id], onlyIfFree: true, CancellationToken.None));
        var taken = await orders.AttachTripAsync(third.Order.Id, "trip-4", onlyIfFree: true, CancellationToken.None);
        Assert.IsType<OrderMutation.Success>(taken);
        var blocked = await orders.AttachTripAsync(third.Order.Id, "trip-2", onlyIfFree: true, CancellationToken.None);
        Assert.Equal(OrderAssignment.TakenMessage, Assert.IsType<OrderMutation.Rejected>(blocked).Message);

        var list = await orders.ListAsync(CancellationToken.None);
        var onTrip = list.Where(order => order.TripId == "trip-2").Select(order => order.Id).ToArray();
        Assert.Equal(2, onTrip.Length);
        Assert.Contains(first.Order.Id, onTrip);
        Assert.Contains(second.Order.Id, onTrip);
        Assert.All(list.Where(order => order.TripId == "trip-2"), order =>
        {
            Assert.Equal("veh-1", order.VehicleId);
            Assert.Equal("drv-1", order.DriverId);
        });
        var queued = await orders.ListQueueAsync(CancellationToken.None);
        Assert.Equal(3, queued.Count);
        Assert.All(queued, item => Assert.Equal(OrderSyncActions.Create, item.Action));

        var removed = Assert.IsType<OrderMutation.Success>(
            await orders.AttachTripAsync(first.Order.Id, null, onlyIfFree: false, CancellationToken.None));
        Assert.Null(removed.Order.TripId);
        Assert.Equal("veh-1", removed.Order.VehicleId);
    }

    [Fact]
    public async Task Assigning_a_trip_does_not_queue_a_pohoda_job()
    {
        var live = new PohodaOptions { SyncMode = "Live", Ico = "12345678", Username = "cteni", Password = "heslo" };
        var orders = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(live));
        var created = Assert.IsType<OrderMutation.Success>(await orders.CreateAsync(Plain(), CancellationToken.None));
        Assert.IsType<OrderMutation.Success>(await orders.ConfirmAsync(created.Order.Id, CancellationToken.None));
        var before = await orders.ListQueueAsync(CancellationToken.None);

        var saved = Assert.IsType<OrderMutation.Success>(await orders.UpdateAsync(created.Order.Id, Plain() with { TripId = "trip-2" }, CancellationToken.None));
        var after = await orders.ListQueueAsync(CancellationToken.None);

        Assert.Equal("trip-2", saved.Order.TripId);
        Assert.Equal("veh-1", saved.Order.VehicleId);
        Assert.Equal("drv-1", saved.Order.DriverId);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(OrderSyncActions.Create, Assert.Single(after).Action);
        Assert.Equal(before[0].VersionTo, after[0].VersionTo);
    }

    private static OrderSaveRequest Plain() => new(
        42,
        "Zákazník",
        "Sklad",
        "ČS",
        new DateOnly(2026, 11, 4),
        new DateOnly(2026, 11, 5),
        "motorová nafta",
        12000m,
        10000m,
        0m,
        null,
        null,
        null,
        null,
        null,
        OrderStatuses.Nova,
        null);
}
