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
    public void Saving_a_pair_by_hand_checks_places_and_kilometers()
    {
        var ok = DistanceSave.Validate(new DistanceSaveRequest(" Sklad Kralupy ", "ČS Brno", 180.12345m));
        Assert.Null(ok.Error);
        Assert.Equal(new DistanceWrite("Sklad Kralupy", "ČS Brno", 180.123m), ok.Write);

        Assert.NotNull(DistanceSave.Validate(new DistanceSaveRequest("", "ČS Brno", 10m)).Error);
        Assert.NotNull(DistanceSave.Validate(new DistanceSaveRequest("ČS Brno", " čs  brno ", 10m)).Error);
        Assert.NotNull(DistanceSave.Validate(new DistanceSaveRequest("A", "B", null)).Error);
        Assert.NotNull(DistanceSave.Validate(new DistanceSaveRequest("A", "B", 0m)).Error);
        Assert.NotNull(DistanceSave.Validate(new DistanceSaveRequest("A", "B", 100_001m)).Error);
        Assert.NotNull(DistanceSave.Validate(null).Error);
    }

    [Fact]
    public async Task A_pair_saved_by_hand_is_found_in_both_directions_with_its_author()
    {
        var orders = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        await orders.SaveDistanceAsync(new DistanceWrite("Sklad Test", "ČS Test", 42.5m), "Jana", CancellationToken.None);

        var hits = await orders.LookupDistancesAsync([new DistanceQuery("čs test", "SKLAD TEST")], CancellationToken.None);
        Assert.Equal(42.5m, hits.Single().Km);
        Assert.Equal("Jana", orders.FindDistance("ČS Test", "Sklad Test")!.UpdatedBy);
    }

    [Fact]
    public async Task Loading_places_come_from_the_store_by_name()
    {
        var orders = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        var places = await orders.ListLoadingPlacesAsync(CancellationToken.None);
        Assert.Contains(places, item => item.Name == "Sklad Kralupy");
        Assert.Contains("Disp_LoadingPlace WHERE Active = 1", File.ReadAllText(Path.Combine(Root(), "Dispatch", "SqlOrderStore.cs")), StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PHMTRANS.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Kořen projektu nenalezen.");
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
    public async Task An_order_can_load_several_fuels_and_checks_each_one()
    {
        var orders = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        var request = Request(
            [new OrderStopInput("ČS Brno", 8000m, null, null, 1), new OrderStopInput("ČS Olomouc", 6000m, null, null, 2)],
            [new OrderLegInput(180m, null, false), new OrderLegInput(40m, null, false)]) with
        {
            Liters = null,
            FuelType = null,
            Loads = [new OrderLoadInput(1, 10000m), new OrderLoadInput(2, 5000m)]
        };

        var created = Assert.IsType<OrderMutation.Success>(await orders.CreateAsync(request, CancellationToken.None, "Jana")).Order;

        Assert.Equal(15000m, created.Liters);
        Assert.Equal((1, FuelTypeCatalog.Diesel), (created.FuelTypeId, created.FuelType));
        Assert.Equal([FuelTypeCatalog.Diesel, FuelTypeCatalog.Petrol], created.Loads!.Select(load => load.FuelType));
        var petrol = created.Quote!.Fuels!.Single(fuel => fuel.FuelTypeId == 2);
        Assert.Equal((5000m, 6000m), (petrol.Loaded, petrol.Delivered));
        Assert.True(created.Quote.DeliveredExceedsLoaded);

        var detail = await orders.GetAsync(created.Id, CancellationToken.None);
        var reread = OrderRoute.Read(detail!.Versions[0] with { Loads = null }, OrderRoute.Write(created));
        Assert.Equal(2, reread.Loads!.Count);

        var moved = Assert.IsType<OrderMutation.Success>(await orders.UpdateAsync(
            created.Id,
            request with { Loads = [new OrderLoadInput(1, 9000m), new OrderLoadInput(2, 6000m)] },
            CancellationToken.None,
            "Jana")).Order;
        Assert.Equal(created.CurrentVersion + 1, moved.CurrentVersion);
        Assert.False(moved.Quote!.DeliveredExceedsLoaded);
    }

    [Fact]
    public void Load_rows_need_a_known_fuel_once_and_valid_liters()
    {
        var types = FuelTypeCatalog.Seed;
        Assert.NotNull(OrderLoads.Build([], types).Error);
        Assert.NotNull(OrderLoads.Build([new OrderLoadInput(null, 10m)], types).Error);
        Assert.NotNull(OrderLoads.Build([new OrderLoadInput(99, 10m)], types).Error);
        Assert.NotNull(OrderLoads.Build([new OrderLoadInput(1, null)], types).Error);
        Assert.NotNull(OrderLoads.Build([new OrderLoadInput(1, -1m)], types).Error);
        Assert.Contains("dvakrát", OrderLoads.Build([new OrderLoadInput(1, 10m), new OrderLoadInput(1, 5m)], types).Error);
        Assert.Null(OrderLoads.Build([new OrderLoadInput(1, 10m), new OrderLoadInput(2, 5m)], types).Error);
    }

    [Fact]
    public void An_old_order_without_load_rows_uses_its_header_fuel()
    {
        var order = Blank() with { FuelType = FuelTypeCatalog.Diesel, FuelTypeId = 1, Liters = 9000m };
        var loads = OrderLoads.Effective(order);
        Assert.Equal((1, FuelTypeCatalog.Diesel, 9000m), (loads.Single().FuelTypeId, loads.Single().FuelType, loads.Single().Liters));
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
