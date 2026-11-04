using Microsoft.Extensions.Options;
using PHMTRANS.Dispatch;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderFuelDistanceTests
{
    [Fact]
    public void Place_pair_trims_case_and_treats_both_directions_as_one()
    {
        var forward = PlacePair.Canonical("  ČS Brno ", "Sklad  Kralupy");
        var reverse = PlacePair.Canonical("SKLAD KRALUPY", "čs brno");

        Assert.Equal(forward, reverse);
        Assert.Equal(("sklad kralupy", "čs brno"), forward);
        Assert.Null(PlacePair.Canonical("  ", "ČS Brno"));
    }

    [Fact]
    public void Manual_kilometers_are_stored_and_a_matching_value_stays_on_the_matrix()
    {
        var known = 180m;
        var typed = DistanceChoice.Resolve(200m, KmSources.Manual, known);
        var same = DistanceChoice.Resolve(known, null, known);
        var missing = DistanceChoice.Resolve(90m, null, null);
        var claimed = DistanceChoice.Resolve(200m, KmSources.Matrix, known);

        Assert.Equal((200m, KmSources.Manual, true), (typed.Kilometers, typed.Source, typed.Upsert));
        Assert.Equal((known, KmSources.Matrix, false), (same.Kilometers, same.Source, same.Upsert));
        Assert.Equal((90m, KmSources.Manual, true), (missing.Kilometers, missing.Source, missing.Upsert));
        Assert.Equal((known, KmSources.Matrix, false), (claimed.Kilometers, claimed.Source, claimed.Upsert));
    }

    [Fact]
    public void Distance_book_remembers_the_writer_and_the_reverse_direction()
    {
        var book = new DistanceBook();
        var at = DateTimeOffset.Parse("2026-11-04T08:00:00+01:00");
        book.Apply([new DistanceWrite("Sklad Kralupy", "ČS Brno", 180m)], at, "Jana Nováková");

        var found = book.Find("čs brno", "  sklad kralupy ");
        Assert.NotNull(found);
        Assert.Equal(180m, found.Km);
        Assert.Equal("Jana Nováková", found.UpdatedBy);
        Assert.Equal(at, found.UpdatedAt);
        Assert.Equal(("sklad kralupy", "čs brno"), (found.FromPlace, found.ToPlace));

        book.Apply([new DistanceWrite("ČS Brno", "Sklad Kralupy", 210m)], at.AddMinutes(5), "Petr");
        Assert.Equal(210m, book.Find("Sklad Kralupy", "ČS Brno")!.Km);
        Assert.Equal("Petr", book.Find("Sklad Kralupy", "ČS Brno")!.UpdatedBy);
    }

    [Fact]
    public void Stop_fuel_defaults_from_the_header_and_a_row_can_override_it()
    {
        var catalog = OrderCatalog.Empty with { FuelTypes = FuelTypeCatalog.Seed };
        var request = Request(
            [new OrderStopInput("ČS Brno", 8000m, null, null), new OrderStopInput("ČS Olomouc", 4000m, null, null, 2)],
            [new OrderLegInput(180m, null, false), new OrderLegInput(40m, null, false)]);
        var routed = OrderRoute.Materialize(
            Blank() with { FuelType = FuelTypeCatalog.Diesel, FuelTypeId = 1 },
            request,
            catalog,
            distances: new Dictionary<string, decimal>(StringComparer.Ordinal)).Order!;

        Assert.Equal((1, FuelTypeCatalog.Diesel), (routed.Stops![0].FuelTypeId, routed.Stops[0].FuelType));
        Assert.Equal((2, FuelTypeCatalog.Petrol), (routed.Stops[1].FuelTypeId, routed.Stops[1].FuelType));
        Assert.Equal(KmSources.Manual, routed.Legs![0].KmSource);
        Assert.Equal(5040m, routed.Legs[0].CostPrice);
        Assert.Equal(1120m, routed.Legs[1].CostPrice);
    }

    [Fact]
    public async Task Saved_kilometers_fill_the_matrix_for_the_next_order_and_an_override_updates_it()
    {
        var orders = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        var created = Assert.IsType<OrderMutation.Success>(await orders.CreateAsync(
            Request(
                [new OrderStopInput("ČS Brno", 8000m, null, null)],
                [new OrderLegInput(180m, null, false, KmSources.Manual)]),
            CancellationToken.None,
            "Jana"));

        Assert.Equal(FuelTypeCatalog.Diesel, created.Order.FuelType);
        Assert.Equal(1, created.Order.FuelTypeId);
        Assert.Equal(1, created.Order.Stops![0].FuelTypeId);
        Assert.Equal(KmSources.Manual, created.Order.Legs![0].KmSource);
        Assert.Equal(5040m, created.Order.Legs[0].CostPrice);

        var learned = await orders.LookupDistancesAsync([new DistanceQuery("čs brno", "  sklad kralupy ")], CancellationToken.None);
        Assert.Equal(180m, Assert.Single(learned).Km);

        var next = Assert.IsType<OrderMutation.Success>(await orders.CreateAsync(
            Request(
                [new OrderStopInput("ČS Brno", 8000m, null, null, 2)],
                [new OrderLegInput(180m, null, false)]),
            CancellationToken.None));
        Assert.Equal(KmSources.Matrix, next.Order.Legs![0].KmSource);
        Assert.Equal(FuelTypeCatalog.Petrol, next.Order.Stops![0].FuelType);
        Assert.Equal(8280m, next.Order.Legs[0].SalePrice);

        var changed = Assert.IsType<OrderMutation.Success>(await orders.UpdateAsync(
            next.Order.Id,
            Request(
                [new OrderStopInput("ČS Brno", 8000m, null, null, 2)],
                [new OrderLegInput(210m, null, false, KmSources.Manual)]),
            CancellationToken.None,
            "Petr"));
        Assert.Equal(KmSources.Manual, changed.Order.Legs![0].KmSource);
        Assert.Equal(5880m, changed.Order.Legs[0].CostPrice);
        var updated = await orders.LookupDistancesAsync([new DistanceQuery("Sklad Kralupy", "ČS Brno")], CancellationToken.None);
        Assert.Equal(210m, Assert.Single(updated).Km);

        var bundle = (await orders.GetAsync(created.Order.Id, CancellationToken.None))!;
        var snapshot = Assert.Single(bundle.Versions);
        Assert.Equal(FuelTypeCatalog.Diesel, snapshot.FuelType);
        Assert.Equal(1, snapshot.FuelTypeId);
        Assert.Equal(FuelTypeCatalog.Diesel, Assert.Single(snapshot.Stops!).FuelType);
        Assert.Equal(KmSources.Manual, Assert.Single(snapshot.Legs!).KmSource);
        Assert.Equal(180m, snapshot.Legs![0].Kilometers);
    }

    [Fact]
    public async Task Unknown_fuel_is_rejected()
    {
        var orders = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        var rejected = Assert.IsType<OrderMutation.Rejected>(await orders.CreateAsync(
            Request([new OrderStopInput("ČS Brno", 8000m, null, null)], [new OrderLegInput(10m, null, false)]) with { FuelType = "nafta extra" },
            CancellationToken.None));
        Assert.Equal("Vyberte druh paliva.", rejected.Message);
    }

    [Fact]
    public void Pohoda_item_text_uses_the_stop_fuel()
    {
        var order = Blank() with
        {
            FuelType = FuelTypeCatalog.Diesel,
            Stops = [new OrderStop("st", 1, "ČS Brno", 8000m, null, null, 2, FuelTypeCatalog.Petrol)],
            Legs = [new OrderLeg("lg", 1, "Sklad Kralupy", "ČS Brno", 8000m, 180m, 8280m, false, 5040m, KmSources.Matrix)]
        };

        Assert.Equal("Leg 1: Sklad Kralupy → ČS Brno, Benzín, 8000 l", Assert.Single(OrderLegItems.SaleLines(order)).Text);
    }

    private static DispatchOrder Blank() => new(
        "ord-1",
        "Z2026-0001",
        42,
        "Zákazník",
        "Sklad Kralupy",
        "ČS Brno",
        new DateOnly(2026, 11, 4),
        new DateOnly(2026, 11, 5),
        FuelTypeCatalog.Diesel,
        8000m,
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
        DateTimeOffset.Parse("2026-11-04T06:00:00+01:00"),
        PricingMode: OrderPricing.ByKm,
        SalePerKm: 46m,
        CostPerKm: 28m);

    private static OrderSaveRequest Request(IReadOnlyList<OrderStopInput> stops, IReadOnlyList<OrderLegInput> legs) =>
        new(
            42,
            "Zákazník",
            "Sklad Kralupy",
            null,
            new DateOnly(2026, 11, 4),
            new DateOnly(2026, 11, 5),
            "motorová nafta",
            8000m,
            null,
            0m,
            null,
            null,
            null,
            null,
            null,
            OrderStatuses.Nova,
            null,
            OrderPricing.ByKm,
            46m,
            0m,
            28m,
            stops,
            legs);
}
