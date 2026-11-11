using PHMTRANS.Dispatch;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderCompartmentTests
{
    private static readonly IReadOnlyList<FuelTypeItem> Fuels = FuelTypeCatalog.Seed;

    private static OrderCatalog Catalog() => OrderCatalog.Empty with
    {
        FuelTypes = Fuels,
        VehicleCompartments = new Dictionary<string, IReadOnlyList<VehicleCompartment>>(StringComparer.Ordinal)
        {
            ["veh-1"] = [new(1, 7000m), new(2, 7000m), new(3, 6000m)],
            ["veh-2"] = []
        }
    };

    [Fact]
    public void Build_sorts_compartments_and_rejects_one_compartment_for_two_fuels()
    {
        var ok = OrderLoads.Build([new OrderLoadInput(Fuels[0].Id, 10000m, [2, 1, 2])], Fuels);
        Assert.Null(ok.Error);
        Assert.Equal([1, 2], ok.Loads![0].Compartments!);

        var clash = OrderLoads.Build(
            [new OrderLoadInput(Fuels[0].Id, 5000m, [1]), new OrderLoadInput(Fuels[1].Id, 5000m, [1, 3])],
            Fuels);
        Assert.Contains("Přepážka 1 je u dvou paliv", clash.Error);

        Assert.NotNull(OrderLoads.Build([new OrderLoadInput(Fuels[0].Id, 1000m, [11])], Fuels).Error);
        Assert.Null(OrderLoads.Build([new OrderLoadInput(Fuels[0].Id, 1000m, null)], Fuels).Loads![0].Compartments);
    }

    [Fact]
    public void Compartments_must_exist_on_the_vehicle_and_hold_the_liters()
    {
        var catalog = Catalog();
        OrderLoad Load(decimal liters, params int[] numbers) => new(1, Fuels[0].Id, Fuels[0].Name, liters, numbers);

        Assert.Null(OrderLoads.CheckCompartments([Load(14000m, 1, 2)], "veh-1", catalog));
        Assert.Null(OrderLoads.CheckCompartments([new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 99999m)], null, catalog));
        Assert.Contains("se nevejde do přepážek 1, 2", OrderLoads.CheckCompartments([Load(14001m, 1, 2)], "veh-1", catalog));
        Assert.Equal("Vozidlo nemá přepážku 4.", OrderLoads.CheckCompartments([Load(1000m, 4)], "veh-1", catalog));
        Assert.Contains("Vyberte vozidlo", OrderLoads.CheckCompartments([Load(1000m, 1)], null, catalog));
        Assert.Contains("nemá zadané přepážky", OrderLoads.CheckCompartments([Load(1000m, 1)], "veh-2", catalog));
    }

    [Fact]
    public void Every_loaded_fuel_needs_a_compartment_once_compartments_are_used()
    {
        var catalog = Catalog();
        var diesel = new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 10000m, [1, 2]);
        var petrol = new OrderLoad(2, Fuels[1].Id, Fuels[1].Name, 2000m);

        Assert.Contains("vyberte přepážku", OrderLoads.CheckCompartments([diesel, petrol], "veh-1", catalog));
        var full = diesel with { Compartments = [1, 2, 3] };
        Assert.Contains("Všechny přepážky vozidla už mají jiné palivo", OrderLoads.CheckCompartments([full, petrol], "veh-1", catalog));
        Assert.Null(OrderLoads.CheckCompartments([diesel, petrol with { Liters = 0m }], "veh-1", catalog));
        Assert.Null(OrderLoads.CheckCompartments([diesel, petrol with { Compartments = [3] }], "veh-1", catalog));
    }

    [Fact]
    public void Compartment_text_round_trips_and_counts_as_a_change()
    {
        Assert.Equal("1,3", OrderLoads.CompartmentsText([1, 3]));
        Assert.Equal([1, 3], OrderLoads.ParseCompartments(" 3, 1,1, x, 12")!);
        Assert.Null(OrderLoads.ParseCompartments(""));

        var order = Order([new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 1000m, [1])]);
        Assert.True(OrderLoads.SameLoads(order, Order([new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 1000m, [1])])));
        Assert.False(OrderLoads.SameLoads(order, Order([new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 1000m, [2])])));
    }

    [Fact]
    public void Unloading_can_have_several_fuels_each_from_its_compartment()
    {
        var catalog = Catalog();
        var diesel = new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 10000m, [1, 2]);
        var petrol = new OrderLoad(2, Fuels[1].Id, Fuels[1].Name, 5000m, [3]);
        OrderStopItem Item(int fuel, int? compartment, decimal liters) => new(Fuels[fuel].Id, Fuels[fuel].Name, compartment, liters);
        DispatchOrder With(params OrderStopItem[][] stops) => Order([diesel, petrol]) with
        {
            VehicleId = "veh-1",
            Stops = stops.Select((items, index) => new OrderStop($"s{index}", index + 1, $"ČS {index + 1}", items.Sum(item => item.Liters), null, null,
                items[0].FuelTypeId, items[0].FuelType, items)).ToArray()
        };

        var ok = With([Item(0, 1, 6000m), Item(1, 3, 2000m)], [Item(0, 2, 4000m), Item(1, 3, 3000m)]);
        Assert.Null(OrderLoads.CheckBalance(ok));
        Assert.Null(OrderLoads.CheckUnloadCompartments(ok, catalog));
        Assert.Equal("Nafta + Benzín", OrderStopItems.FuelText(ok.Stops![0]).Replace(Fuels[0].Name, "Nafta").Replace(Fuels[1].Name, "Benzín"));

        Assert.Contains("vyberte přepážku", OrderLoads.CheckUnloadCompartments(With([Item(0, null, 10000m), Item(1, 3, 5000m)]), catalog));
        Assert.Contains("v přepážce 3 je", OrderLoads.CheckUnloadCompartments(With([Item(0, 3, 10000m), Item(1, 3, 5000m)]), catalog));
        Assert.Contains("se v nakládce neplní", OrderLoads.CheckUnloadCompartments(With([Item(0, 4, 10000m)]), catalog));
        Assert.Contains("pojme jen", OrderLoads.CheckUnloadCompartments(With([Item(0, 1, 8000m), Item(0, 2, 2000m), Item(1, 3, 5000m)]), catalog));
        Assert.Contains("vyloženo o", OrderLoads.CheckBalance(With([Item(0, 1, 6000m), Item(1, 3, 5000m)])));
    }

    [Fact]
    public void Stop_items_are_built_from_the_request_and_round_trip_as_json()
    {
        var request = new OrderSaveRequest(
            42, "Zákazník", "Sklad", null, new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4), Fuels[0].Name, 15000m, null, 0m,
            null, null, null, null, OrderStatuses.Nova, null, OrderPricing.ByKm, 0m, 0m, 0m,
            [new OrderStopInput("ČS 1", null, "Vjezd ze zadu", null, Items: [new(Fuels[0].Id, 1, 10000m), new(Fuels[1].Id, 3, 5000m)])],
            [new OrderLegInput(10m, null, false)]);
        var catalog = Catalog();
        var built = OrderRoute.Materialize(Order([]) with { Loads = null }, request, catalog).Order!;
        var stop = Assert.Single(built.Stops!);
        Assert.Equal(15000m, stop.Liters);
        Assert.Equal(2, stop.Items!.Count);
        Assert.Equal(3, stop.Items[1].Compartment);
        Assert.Equal(stop.Items, OrderStopItems.Read(OrderStopItems.Write(stop.Items))!);

        var twice = request with { Stops = [new OrderStopInput("ČS 1", null, null, null, Items: [new(Fuels[0].Id, 1, 1m), new(Fuels[0].Id, 1, 2m)])] };
        Assert.Contains("je tam dvakrát", OrderRoute.Materialize(Order([]), twice, catalog).Error);
    }

    private static DispatchOrder Order(IReadOnlyList<OrderLoad> loads) =>
        new("o1", "Z2026-0001", 42, "Zákazník", "Sklad", "ČS", new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4),
            Fuels[0].Name, 1000m, 0m, 0m, null, null, null, null, OrderStatuses.Nova, false, 1, null, null, null, null,
            DateTimeOffset.Now, DateTimeOffset.Now, FuelTypeId: Fuels[0].Id, Loads: loads);
}
