using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Microsoft.Extensions.Options;
using PHMTRANS.Dispatch;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderCancelTests
{
    private static readonly DateOnly Loading = new(2026, 11, 4);
    private static readonly DateOnly Unloading = new(2026, 11, 5);

    [Fact]
    public async Task Draft_can_be_deleted_or_cancelled_without_a_pohoda_job()
    {
        var store = Store();
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None));

        var denied = await store.UpdateAsync(created.Order.Id, Request(status: OrderStatuses.Stornovano), CancellationToken.None);
        Assert.Equal("Storno zakázky se dělá tlačítkem Stornovat.", Assert.IsType<OrderMutation.Rejected>(denied).Message);

        var cancelled = Assert.IsType<OrderMutation.Success>(
            await store.CancelAsync(created.Order.Id, "  zákazník odřekl  ", delete: false, CancellationToken.None));
        Assert.Equal(OrderStatuses.Stornovano, cancelled.Order.Status);
        Assert.Equal("zákazník odřekl", cancelled.Order.CancelReason);
        Assert.Equal(2, cancelled.Order.CurrentVersion);
        var bundle = await store.GetAsync(created.Order.Id, CancellationToken.None);
        Assert.NotNull(bundle);
        Assert.Contains(bundle.Versions, version => version.CurrentVersion == 2 && version.Status == OrderStatuses.Stornovano);
        Assert.DoesNotContain(bundle.Sync, item => OrderQueueStatuses.IsOpen(item.Status));

        var locked = await store.UpdateAsync(created.Order.Id, Request(), CancellationToken.None);
        Assert.Equal("Stornovaná zakázka je jen ke čtení.", Assert.IsType<OrderMutation.Rejected>(locked).Message);

        var other = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None));
        var removed = Assert.IsType<OrderMutation.Deleted>(await store.CancelAsync(other.Order.Id, null, delete: true, CancellationToken.None));
        Assert.Equal(other.Order.Id, removed.Id);
        Assert.Null(await store.GetAsync(other.Order.Id, CancellationToken.None));

        var list = await store.ListAsync(CancellationToken.None);
        var visible = OrderListFilter.Apply(list, (IReadOnlyCollection<string>?)null);
        Assert.Contains(visible, order => order.Id == created.Order.Id);
        var active = OrderListFilter.Apply(list, OrderStatuses.Editable);
        Assert.DoesNotContain(active, order => order.Id == created.Order.Id);
        var filtered = OrderListFilter.Apply(list, new[] { OrderStatuses.Nova, OrderStatuses.Stornovano });
        Assert.Contains(filtered, order => order.Id == created.Order.Id);
        Assert.DoesNotContain(OrderListFilter.Apply(list, new[] { OrderStatuses.Nova }), order => order.Id == created.Order.Id);
    }

    [Fact]
    public async Task Confirmed_but_unsent_order_drops_the_queue_on_cancel_and_can_be_deleted()
    {
        var store = Store();
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None));
        await store.ConfirmAsync(created.Order.Id, CancellationToken.None);
        Assert.NotEmpty(await store.ListQueueAsync(CancellationToken.None));

        var cancelled = Assert.IsType<OrderMutation.Success>(await store.CancelAsync(created.Order.Id, null, false, CancellationToken.None));
        Assert.Equal(OrderStatuses.Stornovano, cancelled.Order.Status);
        Assert.Null(cancelled.Order.PohodaOrderId);
        var queue = await store.ListQueueAsync(CancellationToken.None);
        Assert.All(queue, item => Assert.Equal(OrderQueueStatuses.Failed, item.Status));
        Assert.Contains("před odesláním", queue[0].LastError, StringComparison.Ordinal);

        var fresh = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None));
        await store.ConfirmAsync(fresh.Order.Id, CancellationToken.None);
        Assert.IsType<OrderMutation.Deleted>(await store.CancelAsync(fresh.Order.Id, null, true, CancellationToken.None));
        Assert.DoesNotContain(await store.ListQueueAsync(CancellationToken.None), item => item.OrderId == fresh.Order.Id);
    }

    [Fact]
    public async Task Sent_unlocked_cancel_queues_delete_and_not_found_is_success()
    {
        var store = Store();
        var created = await SendOrder(store);
        await store.UpdateAsync(created.Id, Request(), CancellationToken.None);

        var removed = await store.CancelAsync(created.Id, "duplicita", delete: true, CancellationToken.None);
        Assert.Equal("Zakázku odeslanou do Pohody nelze smazat, jen stornovat.", Assert.IsType<OrderMutation.Rejected>(removed).Message);

        var cancelled = Assert.IsType<OrderMutation.Success>(await store.CancelAsync(created.Id, "duplicita", false, CancellationToken.None));
        Assert.Equal(OrderStatuses.Stornovano, cancelled.Order.Status);
        Assert.Equal("duplicita", cancelled.Order.CancelReason);
        var job = (await store.ListQueueAsync(CancellationToken.None)).Single(item => item.Action == OrderSyncActions.Delete && OrderQueueStatuses.IsOpen(item.Status));
        var document = XDocument.Parse(job.PayloadXml);
        var items = document.Descendants().Where(element => element.Name.LocalName == "dataPackItem").ToList();
        Assert.Single(items);
        Assert.Contains("ord:delete", job.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:add", job.PayloadXml, StringComparison.Ordinal);
        Assert.Equal("prijate_objednavky", document.Descendants().First(element => element.Name.LocalName == "filter").Attribute("agenda")?.Value);
        Assert.Equal("15", document.Descendants().First(element => element.Name.LocalName == "id").Value);
        AssertOrderSchema(job.PayloadXml);

        var missing = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="error" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd">
              <rsp:responsePackItem id="Z-del" state="error" note="Záznam nebyl nalezen."/>
            </rsp:responsePack>
            """);
        Assert.True(missing.Ok);
        Assert.Null(missing.Error);
    }

    [Fact]
    public async Task Locked_cancel_creates_a_storno_only_correction_and_does_not_delete()
    {
        var store = Store();
        var created = await SendOrder(store);
        var invoiced = Assert.IsType<OrderMutation.Success>(await store.MarkInvoicedAsync(created.Id, "FV-1", CancellationToken.None));
        Assert.True(invoiced.Order.IsLocked);

        var cancelled = Assert.IsType<OrderMutation.Success>(await store.CancelAsync(created.Id, "vyfakturováno omylem", false, CancellationToken.None));
        Assert.Equal(OrderStatuses.Stornovano, cancelled.Order.Status);
        Assert.True(cancelled.Order.IsLocked);
        var job = (await store.ListQueueAsync(CancellationToken.None)).Single(item => item.Action == OrderSyncActions.Cancel);
        Assert.Equal(OrderSyncActions.Cancel, job.Action);
        Assert.Contains($"{created.Code}/1", job.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:delete", job.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain(">15<", job.PayloadXml, StringComparison.Ordinal);
        Assert.Contains("Storno: Leg 1:", job.PayloadXml, StringComparison.Ordinal);
        var quantities = XDocument.Parse(job.PayloadXml).Descendants()
            .Where(element => element.Name.LocalName == "quantity")
            .Select(element => element.Value)
            .ToArray();
        Assert.NotEmpty(quantities);
        Assert.All(quantities, quantity => Assert.StartsWith("-", quantity, StringComparison.Ordinal));
        Assert.Equal(OrderPohodaXml.LockedOrderWarning($"{created.Code}/1"), job.Warning);
        AssertOrderSchema(job.PayloadXml);

        var again = await store.ResendAsync(created.Id, CancellationToken.None);
        Assert.Equal("Stornovaná zakázka je jen ke čtení.", Assert.IsType<OrderMutation.Rejected>(again).Message);
    }


    [Fact]
    public async Task Partner_identity_links_the_address_book_id_and_matches_the_schema()
    {
        var order = new DispatchOrder(
            "ord-1",
            "Z2026-0007",
            42,
            "Velkoobchod Demo s.r.o.",
            "Sklad Cerekvice",
            "ČS Praha",
            Loading,
            Unloading,
            "motorová nafta",
            12000m,
            10000m,
            0m,
            null,
            null,
            null,
            null,
            OrderStatuses.KFakturaci,
            false,
            2,
            null,
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-11-04T08:00:00+01:00"),
            DateTimeOffset.Parse("2026-11-04T08:00:00+01:00"),
            CustomerIco: "12345678",
            CustomerDic: "CZ12345678",
            CustomerStreet: "Skladová 12",
            CustomerCity: "Cerekvice nad Loučnou",
            CustomerZip: "569 53",
            CustomerEmail: "info@demo.cz",
            CustomerPhone: "+420 601 000 001");
        var xml = OrderPohodaXml.Build(new PohodaOptions { Ico = "26950430", Application = "PHMTRANS" }, new OrderSyncPlan(OrderSyncAction.Create, null, 2), order, null);
        var partner = XDocument.Parse(xml).Descendants().First(element => element.Name.LocalName == "partnerIdentity");
        Assert.Equal("42", partner.Elements().First(element => element.Name.LocalName == "id").Value);
        var address = partner.Elements().First(element => element.Name.LocalName == "address");
        Assert.Equal("Velkoobchod Demo s.r.o.", address.Elements().First(element => element.Name.LocalName == "company").Value);
        Assert.Equal("Skladová 12", address.Elements().First(element => element.Name.LocalName == "street").Value);
        Assert.Equal("Cerekvice nad Loučnou", address.Elements().First(element => element.Name.LocalName == "city").Value);
        Assert.Equal("569 53", address.Elements().First(element => element.Name.LocalName == "zip").Value);
        Assert.Equal("12345678", address.Elements().First(element => element.Name.LocalName == "ico").Value);
        Assert.Equal("CZ12345678", address.Elements().First(element => element.Name.LocalName == "dic").Value);
        AssertOrderSchema(xml);

        var missing = Assert.IsType<OrderMutation.Rejected>(await Store().CreateAsync(Request() with { CustomerPohodaId = null }, CancellationToken.None));
        Assert.Equal("Zákazníka vyberte z adresáře Pohody.", missing.Message);
    }

    [Fact]
    public void Address_book_row_maps_pohoda_columns_and_the_query_is_read_only()
    {
        var row = new PohodaAddressRow
        {
            ID = 15,
            Firma = "  Agro Demo s.r.o.  ",
            Jmeno = "Jan Novák",
            Ulice = "Hlavní 1",
            Obec = "Brno",
            PSC = "602 00",
            ICO = "12345678",
            DIC = "CZ12345678",
            Email = "agro@demo.cz",
            Tel = "+420 777 000 111"
        };
        var address = row.ToAddress();
        Assert.NotNull(address);
        Assert.Equal(15, address.Id);
        Assert.Equal("Agro Demo s.r.o.", address.Name);
        Assert.Equal("Jan Novák", address.Person);
        Assert.Equal("Hlavní 1", address.Street);
        Assert.Equal("Brno", address.City);
        Assert.Equal("602 00", address.Zip);
        Assert.Equal("12345678", address.Ico);
        Assert.Equal("CZ12345678", address.Dic);
        Assert.Equal("agro@demo.cz", address.Email);
        Assert.Equal("+420 777 000 111", address.Phone);

        var person = new PohodaAddressRow { ID = 8, Jmeno = "  Eva Malá  " }.ToAddress();
        Assert.Equal("Eva Malá", person!.Name);
        Assert.Null(person.Company);
        Assert.Null(new PohodaAddressRow { ID = 0, Firma = "X" }.ToAddress());

        Assert.Equal("StwPh_26950430_2026", PohodaAddressSql.DefaultDatabase);
        Assert.Equal("StwPh_26950430_2026", new PohodaOptions().AddressBookDatabase);
        var settingsDir = new DirectoryInfo(AppContext.BaseDirectory);
        string? settings = null;
        string? development = null;
        while (settingsDir is not null && (settings is null || development is null))
        {
            var app = Path.Combine(settingsDir.FullName, "appsettings.json");
            var dev = Path.Combine(settingsDir.FullName, "appsettings.Development.json");
            if (settings is null && File.Exists(app))
                settings = File.ReadAllText(app);
            if (development is null && File.Exists(dev))
                development = File.ReadAllText(dev);
            settingsDir = settingsDir.Parent;
        }
        Assert.NotNull(settings);
        Assert.NotNull(development);
        Assert.Contains("\"AddressBookDatabase\": \"StwPh_26950430_2026\"", settings, StringComparison.Ordinal);
        Assert.Contains("\"AddressBookDatabase\": \"StwPh_26950430_2026\"", development, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AddressBookDatabase\": \"Smerco_26950430_2026\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", settings, StringComparison.Ordinal);
        var sql = PohodaAddressSql.SearchSql(PohodaAddressSql.DefaultDatabase);
        Assert.Contains("SELECT TOP (20)", sql, StringComparison.Ordinal);
        Assert.Contains("[StwPh_26950430_2026].dbo.AD", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Smerco_26950430_2026.dbo.AD", sql, StringComparison.Ordinal);
        Assert.Contains("Firma", sql, StringComparison.Ordinal);
        Assert.Contains("Jmeno", sql, StringComparison.Ordinal);
        Assert.Contains("ICO", sql, StringComparison.Ordinal);
        Assert.Contains("Ulice", sql, StringComparison.Ordinal);
        Assert.Contains("Obec", sql, StringComparison.Ordinal);
        Assert.Contains("PSC", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("%12!%3%", PohodaAddressSql.Like("12%3"));
        Assert.Contains(
            "GRANT SELECT ON [StwPh_26950430_2026].dbo.AD TO phmtrans_dispatch;",
            PohodaAddressSql.PermissionMessage(PohodaAddressSql.DefaultDatabase),
            StringComparison.Ordinal);
        Assert.True(PohodaAddressSql.IsPermissionError(229));
    }

    private static MemoryOrderStore Store() =>
        new(new MemoryDispatchStore(), Options.Create(new PohodaOptions { SyncMode = "Live", Ico = "26950430", Application = "PHMTRANS" }));

    private static async Task<DispatchOrder> SendOrder(MemoryOrderStore store)
    {
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None));
        await store.ConfirmAsync(created.Order.Id, CancellationToken.None);
        var create = (await store.ListQueueAsync(CancellationToken.None)).Single(item => item.Action == OrderSyncActions.Create);
        await store.MarkSentAsync(create, PohodaImportResult.Success(15, "242100045"), CancellationToken.None);
        return (await store.GetAsync(created.Order.Id, CancellationToken.None))!.Order;
    }

    private static OrderSaveRequest Request(string? status = null) =>
        new(
            42,
            "Velkoobchod Demo s.r.o.",
            "Sklad Cerekvice",
            "ČS Praha",
            Loading,
            Unloading,
            "motorová nafta",
            12000m,
            10000m,
            0m,
            null,
            null,
            null,
            null,
            status ?? OrderStatuses.Nova,
            null,
            CustomerIco: "12345678",
            CustomerStreet: "Skladová 12",
            CustomerCity: "Cerekvice nad Loučnou",
            CustomerZip: "569 53");

    private static readonly XmlSchemaSet OrderSchema = LoadOrderSchema();

    private static XmlSchemaSet LoadOrderSchema()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Schemas", "order.xsd"));
        var errors = new List<string>();
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        set.ValidationEventHandler += (_, args) => errors.Add(args.Message);
        set.Add(null, path);
        set.Compile();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Distinct().Take(12)));
        return set;
    }

    private static void AssertOrderSchema(string xml)
    {
        var orders = XDocument.Parse(xml).Descendants().Where(element => element.Name.LocalName == "order").ToList();
        Assert.NotEmpty(orders);
        foreach (var order in orders)
        {
            var errors = new List<string>();
            new XDocument(new XElement(order)).Validate(OrderSchema, (_, args) => errors.Add(args.Message), true);
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }
    }
}
