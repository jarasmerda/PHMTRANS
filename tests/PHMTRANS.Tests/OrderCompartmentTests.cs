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
    public void Compartment_text_round_trips_and_counts_as_a_change()
    {
        Assert.Equal("1,3", OrderLoads.CompartmentsText([1, 3]));
        Assert.Equal([1, 3], OrderLoads.ParseCompartments(" 3, 1,1, x, 12")!);
        Assert.Null(OrderLoads.ParseCompartments(""));

        var order = Order([new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 1000m, [1])]);
        Assert.True(OrderLoads.SameLoads(order, Order([new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 1000m, [1])])));
        Assert.False(OrderLoads.SameLoads(order, Order([new OrderLoad(1, Fuels[0].Id, Fuels[0].Name, 1000m, [2])])));
    }

    private static DispatchOrder Order(IReadOnlyList<OrderLoad> loads) =>
        new("o1", "Z2026-0001", 42, "Zákazník", "Sklad", "ČS", new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4),
            Fuels[0].Name, 1000m, 0m, 0m, null, null, null, null, OrderStatuses.Nova, false, 1, null, null, null, null,
            DateTimeOffset.Now, DateTimeOffset.Now, FuelTypeId: Fuels[0].Id, Loads: loads);
}
