using Microsoft.Extensions.Options;
using PHMTRANS.Dispatch;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderConfirmDocumentsTests
{
    private static readonly DateOnly Day = new(2026, 11, 11);

    [Fact]
    public void Required_documents_follow_the_unloadings()
    {
        var required = OrderDocumentChecklist.Required(Order(OrderStatuses.Nova, "ČS Brno", "ČS Olomouc"));

        Assert.Equal(
            ["nakladka-nakladni-list", "vykladka-1-staceci-list", "vykladka-1-potvrzeni", "vykladka-2-staceci-list", "vykladka-2-potvrzeni"],
            required.Select(item => item.Key));
        Assert.Equal("Vykládka 2 (ČS Olomouc): potvrzení od zákazníka", required[^1].Label);
        Assert.Equal("vykladka-1-staceci-list", OrderDocumentChecklist.SlotOf("20261111-120001-vykladka-1-staceci-list-2.JPG"));
        Assert.Null(OrderDocumentChecklist.SlotOf("Z2026-0001-foto.jpg"));
    }

    [Fact]
    public void Confirm_needs_a_reason_only_when_documents_are_missing()
    {
        var order = Order(OrderStatuses.Nova, "ČS Brno");
        string[] all = ["20261111-120001-nakladka-nakladni-list.jpg", "20261111-120002-vykladka-1-staceci-list.jpg", "20261111-120003-vykladka-1-potvrzeni.pdf"];

        var missing = OrderDocumentChecklist.ForConfirm(order, all[..2], null);
        Assert.True(missing.NeedsReason);
        Assert.Equal(["Vykládka 1 (ČS Brno): potvrzení od zákazníka"], missing.Missing);

        var withReason = OrderDocumentChecklist.ForConfirm(order, all[..2], "  zákazník nepodepsal  ");
        Assert.False(withReason.NeedsReason);
        Assert.Equal("zákazník nepodepsal", withReason.Reason);

        var complete = OrderDocumentChecklist.ForConfirm(order, all, "zbytečný důvod");
        Assert.False(complete.NeedsReason);
        Assert.Null(complete.Reason);

        Assert.False(OrderDocumentChecklist.ForConfirm(order, null, null).NeedsReason);
        Assert.False(OrderDocumentChecklist.ForConfirm(Order(OrderStatuses.KFakturaci, "ČS Brno"), [], null).NeedsReason);
    }

    [Fact]
    public async Task Reason_is_saved_with_the_confirmation()
    {
        var store = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None)).Order;

        var tooLong = Assert.IsType<OrderMutation.Rejected>(await store.ConfirmAsync(created.Id, CancellationToken.None, new string('x', 501)));
        Assert.Contains("500 znaků", tooLong.Message);

        var confirmed = Assert.IsType<OrderMutation.Success>(await store.ConfirmAsync(created.Id, CancellationToken.None, " řidič dodá zítra ")).Order;
        Assert.Equal(OrderStatuses.KFakturaci, confirmed.Status);
        Assert.Equal("řidič dodá zítra", confirmed.MissingDocumentsReason);
        Assert.Equal("řidič dodá zítra", (await store.GetAsync(created.Id, CancellationToken.None))!.Order.MissingDocumentsReason);

        // Už potvrzená zakázka: nový důvod se uloží jako nová verze.
        var again = Assert.IsType<OrderMutation.Success>(await store.ConfirmAsync(created.Id, CancellationToken.None, "zákazník pošle e-mailem")).Order;
        Assert.Equal("zákazník pošle e-mailem", again.MissingDocumentsReason);
        Assert.Equal(confirmed.CurrentVersion + 1, again.CurrentVersion);

        // Ve formuláři jde důvod upravit i smazat, bez pole zůstane beze změny.
        var kept = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(created.Id, Request() with { Status = OrderStatuses.KFakturaci, Note = "jiná poznámka" }, CancellationToken.None)).Order;
        Assert.Equal("zákazník pošle e-mailem", kept.MissingDocumentsReason);
        var edited = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(created.Id, Request() with { Status = OrderStatuses.KFakturaci, Note = "jiná poznámka", MissingDocumentsReason = " doloženo později " }, CancellationToken.None)).Order;
        Assert.Equal("doloženo později", edited.MissingDocumentsReason);
        var cleared = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(created.Id, Request() with { Status = OrderStatuses.KFakturaci, Note = "jiná poznámka", MissingDocumentsReason = "" }, CancellationToken.None)).Order;
        Assert.Null(cleared.MissingDocumentsReason);
    }

    [Fact]
    public async Task Order_with_zero_sale_cannot_be_confirmed()
    {
        var store = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "DryRun" }));
        var free = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request() with { AgreedPrice = 0m }, CancellationToken.None)).Order;

        var rejected = Assert.IsType<OrderMutation.Rejected>(await store.ConfirmAsync(free.Id, CancellationToken.None));
        Assert.Contains("prodej 0 Kč", rejected.Message);
        Assert.Equal(OrderStatuses.Nova, (await store.GetAsync(free.Id, CancellationToken.None))!.Order.Status);

        var withSurcharge = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request() with { AgreedPrice = 0m, Surcharges = 500m }, CancellationToken.None)).Order;
        Assert.IsType<OrderMutation.Success>(await store.ConfirmAsync(withSurcharge.Id, CancellationToken.None));
    }

    private static DispatchOrder Order(string status, params string[] places) =>
        new("o1", "Z2026-0001", 42, "Zákazník", "Sklad", places[^1], Day, Day, "nafta", 1000m, 0m, 0m, null, null, null, null,
            status, false, 1, null, null, null, null, DateTimeOffset.Now, DateTimeOffset.Now,
            Stops: places.Select((place, index) => new OrderStop($"s{index}", index + 1, place, 500m, null, null)).ToArray());

    private static OrderSaveRequest Request() =>
        new(42, "Velkoobchod Demo s.r.o.", "Sklad Cerekvice", "ČS Praha", Day, Day, "motorová nafta", 12000m, 10000m, 0m,
            null, null, null, null, OrderStatuses.Nova, null);
}
