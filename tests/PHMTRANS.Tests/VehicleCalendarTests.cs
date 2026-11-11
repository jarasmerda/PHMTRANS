using PHMTRANS.Dispatch;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class VehicleCalendarTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);

    [Fact]
    public void Vehicle_is_busy_from_loading_to_unloading_and_cancelled_orders_do_not_count()
    {
        var vehicles = new[] { new DispatchVehicle("veh-1", "1A2 3456", "MAN"), new DispatchVehicle("veh-2", "2B3 4567", "") };
        var orders = new[]
        {
            Order("o1", "veh-1", Monday, Monday.AddDays(1)),
            Order("o2", "veh-1", Monday.AddDays(1), Monday.AddDays(1)),
            Order("o3", "veh-2", Monday.AddDays(2), Monday.AddDays(2)) with { Status = OrderStatuses.Stornovano },
            Order("o4", null, Monday.AddDays(3), Monday.AddDays(3)),
            Order("o5", "veh-2", Monday.AddDays(20), Monday.AddDays(21))
        };

        var calendar = VehicleCalendar.Build(vehicles, orders, Monday, 7);

        Assert.Equal(7, calendar.Dates.Count);
        Assert.Equal(Monday.AddDays(6), calendar.To);
        var man = calendar.Vehicles.Single(row => row.VehicleId == "veh-1");
        Assert.Equal("1A2 3456 · MAN", man.Name);
        Assert.Equal(2, man.BusyDays);
        Assert.Equal(29, man.Percent);
        Assert.Equal(["o1"], man.Days[0].Orders.Select(order => order.Id));
        Assert.Equal(["o1", "o2"], man.Days[1].Orders.Select(order => order.Id));
        var second = calendar.Vehicles.Single(row => row.VehicleId == "veh-2");
        Assert.Equal("2B3 4567", second.Name);
        Assert.Equal(0, second.BusyDays);
        var free = calendar.Vehicles.Single(row => row.VehicleId is null);
        Assert.Equal(VehicleCalendar.UnassignedName, free.Name);
        Assert.Equal(["o4"], free.Days[3].Orders.Select(order => order.Id));
    }

    [Fact]
    public void Range_defaults_to_this_week_and_rejects_bad_input()
    {
        var today = new DateOnly(2026, 11, 6);
        Assert.Equal((Monday, 14, (string?)null), VehicleCalendar.ParseRange(null, null, today));
        Assert.Equal((new DateOnly(2026, 11, 10), 7, (string?)null), VehicleCalendar.ParseRange("2026-11-10", 7, today));
        Assert.NotNull(VehicleCalendar.ParseRange("10.11.2026", 7, today).Error);
        Assert.NotNull(VehicleCalendar.ParseRange(null, 0, today).Error);
        Assert.NotNull(VehicleCalendar.ParseRange(null, VehicleCalendar.MaxDays + 1, today).Error);
        Assert.Equal(Monday, VehicleCalendar.WeekStart(new DateOnly(2026, 11, 8)));
    }

    [Fact]
    public void Events_have_hours_default_times_and_skip_cancelled_or_outside_orders()
    {
        var vehicles = new[] { new DispatchVehicle("veh-1", "1A2 3456", "MAN") };
        var orders = new[]
        {
            Order("o1", "veh-1", Monday, Monday) with { LoadingTime = "05:30", UnloadingTime = "11:00" },
            Order("o2", "veh-9", Monday.AddDays(1), Monday.AddDays(2)),
            Order("o3", "veh-1", Monday, Monday) with { Status = OrderStatuses.Stornovano },
            Order("o4", "veh-1", Monday.AddDays(-1), Monday.AddDays(-1)) with { UnloadingTime = "23:00" },
            Order("o5", "veh-1", Monday.AddDays(7), Monday.AddDays(7))
        };

        var calendar = VehicleCalendar.Events(vehicles, orders, Monday, 7);

        Assert.Equal(["o1", "o2"], calendar.Events.Select(item => item.Id));
        var first = calendar.Events[0];
        Assert.Equal(Monday.ToDateTime(new TimeOnly(5, 30)), first.Start);
        Assert.Equal(Monday.ToDateTime(new TimeOnly(11, 0)), first.End);
        Assert.False(first.Estimated);
        Assert.Equal(("veh-1", "1A2 3456"), (first.VehicleId, first.Vehicle));
        var second = calendar.Events[1];
        Assert.Null(second.VehicleId);
        Assert.True(second.Estimated);
        Assert.Equal(Monday.AddDays(2).ToDateTime(new TimeOnly(14, 0)), second.End);
        Assert.Equal([("veh-1", "1A2 3456")], calendar.Vehicles.Select(vehicle => (vehicle.Id, vehicle.Plate)));
    }

    private static DispatchOrder Order(string id, string? vehicleId, DateOnly loading, DateOnly unloading) => new(
        id, id.ToUpperInvariant(), null, "Zákazník", "Sklad", "ČS", loading, unloading, "motorová nafta",
        10000m, 0m, 0m, null, null, vehicleId, null, OrderStatuses.Nova, false, 1, null, null, null, null,
        DateTimeOffset.Parse("2026-11-01T06:00:00+01:00"), DateTimeOffset.Parse("2026-11-01T06:00:00+01:00"));
}
