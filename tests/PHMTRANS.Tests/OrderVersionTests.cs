using Microsoft.Extensions.Options;
using PHMTRANS.Dispatch;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderVersionTests
{
    [Fact]
    public async Task Older_version_keeps_the_stored_snapshot_after_the_order_changes()
    {
        var orders = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        var created = Assert.IsType<OrderMutation.Success>(await orders.CreateAsync(First(), CancellationToken.None));
        var updated = Assert.IsType<OrderMutation.Success>(await orders.UpdateAsync(created.Order.Id, Second(), CancellationToken.None));

        Assert.Equal(2, updated.Order.CurrentVersion);
        var bundle = (await orders.GetAsync(created.Order.Id, CancellationToken.None))!;
        var first = Assert.Single(bundle.Versions, version => version.CurrentVersion == 1);
        var second = Assert.Single(bundle.Versions, version => version.CurrentVersion == 2);

        Assert.Equal("Původní s.r.o.", first.CustomerName);
        Assert.Equal("Sklad Kralupy", first.LoadingPlace);
        Assert.Equal(new DateOnly(2026, 11, 4), first.LoadingDate);
        var firstStop = Assert.Single(first.Stops!);
        Assert.Equal("ČS Brno", firstStop.Place);
        Assert.Equal(8000m, firstStop.Liters);
        var firstLeg = Assert.Single(first.Legs!);
        Assert.Equal(180m, firstLeg.Kilometers);
        Assert.Equal(8280m, firstLeg.SalePrice);
        Assert.Equal(5040m, firstLeg.CostPrice);
        Assert.Equal(8280m, first.AgreedPrice);

        Assert.Equal("Nový odběratel a.s.", second.CustomerName);
        Assert.Equal("Areál Litvínov", second.LoadingPlace);
        Assert.Equal(new DateOnly(2026, 11, 6), second.LoadingDate);
        Assert.Equal("ČS Liberec", Assert.Single(second.Stops!).Place);
        Assert.Equal(40m, second.Legs![0].Kilometers);
        Assert.Equal(1840m, second.Legs[0].SalePrice);
        Assert.Equal("Původní s.r.o.", first.CustomerName);
        Assert.Equal(180m, firstLeg.Kilometers);
    }

    [Fact]
    public void Route_json_is_the_stored_line_snapshot()
    {
        var stored = OrderRoute.Write(new DispatchOrder(
            "ord-1",
            "Z2026-0001",
            42,
            "Původní s.r.o.",
            "Sklad Kralupy",
            "ČS Brno",
            new DateOnly(2026, 11, 4),
            new DateOnly(2026, 11, 4),
            "motorová nafta",
            8000m,
            8280m,
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
            CostPerKm: 28m,
            Stops: [new OrderStop("st-1", 1, "ČS Brno", 8000m, "volat", null)],
            Legs: [new OrderLeg("lg-1", 1, "Sklad Kralupy", "ČS Brno", 8000m, 180m, 8280m, false, 5040m)]));

        var laterHeader = new DispatchOrder(
            "ord-1",
            "Z2026-0001",
            42,
            "Nový odběratel a.s.",
            "Areál Litvínov",
            "ČS Liberec",
            new DateOnly(2026, 11, 6),
            new DateOnly(2026, 11, 7),
            "benzin",
            9000m,
            1m,
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
            DateTimeOffset.Parse("2026-11-04T08:00:00+01:00"));

        var read = OrderRoute.Read(laterHeader, stored);
        var stop = Assert.Single(read.Stops!);
        Assert.Equal("ČS Brno", stop.Place);
        Assert.Equal("volat", stop.Note);
        Assert.Equal(180m, read.Legs![0].Kilometers);
        Assert.Equal(8280m, read.Legs[0].SalePrice);
        Assert.Equal(5040m, read.Legs[0].CostPrice);
        Assert.Equal("Nový odběratel a.s.", read.CustomerName);
        Assert.Equal(new DateOnly(2026, 11, 6), read.LoadingDate);
    }

    private static OrderSaveRequest First() => Request(
        "Původní s.r.o.",
        "Sklad Kralupy",
        new DateOnly(2026, 11, 4),
        [new OrderStopInput("ČS Brno", 8000m, null, null)],
        [new OrderLegInput(180m, null, false)]);

    private static OrderSaveRequest Second() => Request(
        "Nový odběratel a.s.",
        "Areál Litvínov",
        new DateOnly(2026, 11, 6),
        [new OrderStopInput("ČS Liberec", 8000m, null, null)],
        [new OrderLegInput(40m, null, false)],
        new DateOnly(2026, 11, 7));

    private static OrderSaveRequest Request(
        string customer,
        string loading,
        DateOnly loadingDate,
        IReadOnlyList<OrderStopInput> stops,
        IReadOnlyList<OrderLegInput> legs,
        DateOnly? unloadingDate = null) =>
        new(
            42,
            customer,
            loading,
            null,
            loadingDate,
            unloadingDate ?? new DateOnly(2026, 11, 5),
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
