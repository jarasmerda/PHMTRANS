using Microsoft.Extensions.Options;
using PHMTRANS.Dispatch;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderTimeTests
{
    private static readonly DateOnly Day = new(2026, 11, 4);

    [Theory]
    [InlineData("6:30", "06:30")]
    [InlineData("06:30:00", "06:30")]
    [InlineData(" 14:00 ", "14:00")]
    public void Parse_normalizes_time(string input, string expected)
    {
        var (value, error) = OrderTimes.Parse(input, "Čas");
        Assert.Null(error);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Parse_rejects_bad_time_and_keeps_blank_empty()
    {
        Assert.Equal("Čas zadejte jako HH:MM.", OrderTimes.Parse("25:00", "Čas").Error);
        Assert.Equal((null, null), OrderTimes.Parse(" ", "Čas"));
    }

    [Fact]
    public async Task Times_are_saved_and_a_time_change_is_a_new_version()
    {
        var store = Store();
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request("7:00", "15:30"), CancellationToken.None));
        Assert.Equal("07:00", created.Order.LoadingTime);
        Assert.Equal("15:30", created.Order.UnloadingTime);

        var updated = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(created.Order.Id, Request("8:00", "15:30"), CancellationToken.None));
        Assert.Equal("08:00", updated.Order.LoadingTime);
        Assert.Equal(created.Order.CurrentVersion + 1, updated.Order.CurrentVersion);
    }

    [Fact]
    public async Task Unloading_time_before_loading_on_the_same_day_is_rejected()
    {
        var rejected = Assert.IsType<OrderMutation.Rejected>(await Store().CreateAsync(Request("10:00", "09:00"), CancellationToken.None));
        Assert.Equal("Čas vykládky nemůže být před časem nakládky.", rejected.Message);
    }

    [Fact]
    public void Span_uses_default_times_and_at_least_one_hour()
    {
        var order = Order(null, null);
        Assert.Equal((Day.ToDateTime(new TimeOnly(6, 0)), Day.ToDateTime(new TimeOnly(14, 0))), OrderTimes.Span(order));

        var tight = Order("13:30", "13:30");
        Assert.Equal(Day.ToDateTime(new TimeOnly(14, 30)), OrderTimes.Span(tight).End);
    }

    private static DispatchOrder Order(string? start, string? end) =>
        new("o1", "Z2026-0001", 42, "Zákazník", "Sklad", "ČS", Day, Day, "nafta", 1000m, 0m, 0m, null, null, null, null,
            OrderStatuses.Nova, false, 1, null, null, null, null, DateTimeOffset.Now, DateTimeOffset.Now,
            LoadingTime: start, UnloadingTime: end);

    private static MemoryOrderStore Store() =>
        new(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "Live", Application = "PHMTRANS" }));

    private static OrderSaveRequest Request(string? loadingTime, string? unloadingTime) =>
        new(
            42,
            "Velkoobchod Demo s.r.o.",
            "Sklad Cerekvice",
            "ČS Praha",
            Day,
            Day,
            "motorová nafta",
            12000m,
            10000m,
            0m,
            null,
            null,
            null,
            null,
            OrderStatuses.Nova,
            null,
            LoadingTime: loadingTime,
            UnloadingTime: unloadingTime);
}
