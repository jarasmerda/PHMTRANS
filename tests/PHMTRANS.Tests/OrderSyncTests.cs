using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Microsoft.Extensions.Options;
using PHMTRANS.Dispatch;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderSyncTests
{
    private static readonly DateOnly Loading = new(2026, 11, 4);
    private static readonly DateOnly Unloading = new(2026, 11, 5);

    [Fact]
    public void New_order_is_not_queued_until_it_is_confirmed()
    {
        var draft = Sample(version: 1, price: 10000m, fuel: "motorová nafta");
        var plan = OrderSyncPlanner.Plan(draft, previous: null, lastSent: null, confirmRequested: false);

        Assert.Equal(OrderSyncAction.None, plan.Action);
    }

    [Fact]
    public void Confirmation_creates_a_received_order_when_pohoda_does_not_have_it()
    {
        var previous = Sample(version: 1, price: 10000m, fuel: "motorová nafta");
        var current = previous with { Status = OrderStatuses.KFakturaci, CurrentVersion = 2 };
        var plan = OrderSyncPlanner.Plan(current, previous, lastSent: null, confirmRequested: true);

        Assert.Equal(OrderSyncAction.Create, plan.Action);
        Assert.Null(plan.VersionFrom);
        Assert.Equal(2, plan.VersionTo);
    }

    [Fact]
    public void Note_driver_and_cmr_do_not_queue()
    {
        var sent = Sent(version: 2, price: 10000m, surcharges: 200m, fuel: "motorová nafta");
        var current = sent with
        {
            CurrentVersion = 3,
            Note = "volat před vykládkou",
            DriverId = "drv-2",
            CmrNumber = "CMR-77",
            VehicleId = "veh-9",
            TripId = "trip-9"
        };

        var plan = OrderSyncPlanner.Plan(current, sent, sent, confirmRequested: false);

        Assert.Equal(OrderSyncAction.None, plan.Action);
    }

    [Fact]
    public void Unlocked_change_updates_the_existing_pohoda_document()
    {
        var sent = Sent(version: 2, price: 10000m, surcharges: 0m, fuel: "motorová nafta");
        var current = sent with { CurrentVersion = 3, AgreedPrice = 12000m };
        var plan = OrderSyncPlanner.Plan(current, sent, sent, confirmRequested: false);

        Assert.Equal(OrderSyncAction.Update, plan.Action);
        Assert.Equal(2, plan.VersionFrom);
        Assert.Equal(3, plan.VersionTo);
    }

    [Fact]
    public void Correction_stornos_the_last_sent_version_not_the_intermediate_one()
    {
        var sent = Sent(version: 1, price: 8000m, surcharges: 200m, fuel: "motorová nafta");
        var intermediate = sent with { CurrentVersion = 2, AgreedPrice = 15000m, Note = "ještě neodesláno" };
        var current = intermediate with
        {
            CurrentVersion = 3,
            AgreedPrice = 10000m,
            Surcharges = 500m,
            FuelType = "benzin",
            IsLocked = true,
            Status = OrderStatuses.Vyfakturovano
        };

        var plan = OrderSyncPlanner.Plan(current, intermediate, sent, confirmRequested: false);
        var lines = OrderPohodaXml.Lines(plan.Action, current, sent);

        Assert.Equal(OrderSyncAction.Correction, plan.Action);
        Assert.Equal(1, plan.VersionFrom);
        Assert.Equal(2, lines.Count);
        Assert.Equal(-1m, lines[0].Quantity);
        Assert.Equal(8200m, lines[0].UnitPrice);
        Assert.Equal(-8200m, lines[0].Amount);
        Assert.StartsWith("Storno: ", lines[0].Text, StringComparison.Ordinal);
        Assert.Contains("motorová nafta", lines[0].Text, StringComparison.Ordinal);
        Assert.Equal(10500m, lines[1].UnitPrice);
        Assert.Contains("benzin", lines[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("15000", lines[0].UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal(OrderPohodaFields.Total(current) - OrderPohodaFields.Total(sent), lines[0].Amount + lines[1].Amount);
    }

    [Fact]
    public void Create_xml_is_a_received_order_with_the_dispatch_number()
    {
        var order = Sample(version: 2, price: 10000m, fuel: "motorová nafta") with
        {
            Code = "Z2026-0007",
            Status = OrderStatuses.KFakturaci,
            CustomerPohodaId = 42,
            Surcharges = 250m
        };
        var plan = new OrderSyncPlan(OrderSyncAction.Create, null, 2);
        var xml = OrderPohodaXml.Build(SampleOptions(), plan, order, lastSent: null);
        var document = XDocument.Parse(xml);

        Assert.Contains("receivedOrder", xml, StringComparison.Ordinal);
        Assert.Contains("Z2026-0007", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("actionType", xml, StringComparison.Ordinal);
        AssertOrderSchema(xml);
        Assert.Equal("42", Value(document, "partnerIdentity", "id"));
        var prices = Prices(document);
        Assert.Equal(new[] { 10000m, 250m }, prices);
        Assert.Equal("none", document.Descendants().First(element => element.Name.LocalName == "rateVAT").Value);
    }

    [Fact]
    public void Unlocked_change_is_delete_then_add_in_one_pack()
    {
        var order = Sent(version: 3, price: 9000m, surcharges: 0m, fuel: "nafta") with { Code = "Z2026-0001" };
        var plan = new OrderSyncPlan(OrderSyncAction.Update, 2, 3);
        var xml = OrderPohodaXml.Build(SampleOptions(), plan, order, order);
        var document = XDocument.Parse(xml);
        var items = document.Descendants().Where(element => element.Name.LocalName == "dataPackItem").ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("Z2026-0001-del", items[0].Attribute("id")?.Value);
        Assert.Equal("Z2026-0001-add", items[1].Attribute("id")?.Value);
        Assert.Equal("prijate_objednavky", items[0].Descendants().First(element => element.Name.LocalName == "filter").Attribute("agenda")?.Value);
        Assert.Equal("15", items[0].Descendants().First(element => element.Name.LocalName == "id").Value);
        Assert.Equal("Z2026-0001", items[1].Descendants().First(element => element.Name.LocalName == "numberOrder").Value);
        Assert.Contains("ord:delete", xml, StringComparison.Ordinal);
        Assert.Contains("ord:add", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:update", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<update", xml, StringComparison.OrdinalIgnoreCase);
        AssertOrderSchema(xml);
    }

    [Fact]
    public void Correction_xml_has_two_lines_that_sum_to_the_difference()
    {
        var sent = Sent(version: 1, price: 8000m, surcharges: 200m, fuel: "motorová nafta");
        var current = sent with
        {
            CurrentVersion = 4,
            AgreedPrice = 10000m,
            Surcharges = 500m,
            FuelType = "benzin",
            IsLocked = true
        };
        var plan = new OrderSyncPlan(OrderSyncAction.Correction, 1, 4);
        var xml = OrderPohodaXml.Build(SampleOptions(), plan, current, sent);
        var prices = Prices(XDocument.Parse(xml));

        Assert.Equal(2, prices.Count);
        Assert.Equal(8200m, prices[0]);
        Assert.Equal(10500m, prices[1]);
        Assert.Equal("-1", XDocument.Parse(xml).Descendants().First(element => element.Name.LocalName == "quantity").Value);
        Assert.Equal(2300m, -prices[0] + prices[1]);
        Assert.Contains("Oprava k 242100045", xml, StringComparison.Ordinal);
        Assert.Contains("Storno: motorová nafta", xml, StringComparison.Ordinal);
        Assert.Contains("benzin", xml, StringComparison.Ordinal);
        Assert.Contains("Z2026-0001/1", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:delete", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(">15<", xml, StringComparison.Ordinal);
        Assert.Contains("<ord:add", xml, StringComparison.Ordinal);
        AssertOrderSchema(xml);
    }

    [Fact]
    public void Import_response_reads_the_document_number_and_an_error()
    {
        var ok = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="ok" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd">
              <rsp:responsePackItem state="ok">
                <rdc:producedDetails xmlns:rdc="http://www.stormware.cz/schema/version_2/documentresponse.xsd">
                  <rdc:id>321</rdc:id>
                  <rdc:number>242100045</rdc:number>
                </rdc:producedDetails>
              </rsp:responsePackItem>
            </rsp:responsePack>
            """);
        Assert.True(ok.Ok);
        Assert.Equal(321, ok.Id);
        Assert.Equal("242100045", ok.Number);

        var failed = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="error" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd">
              <rsp:note>Neplatné IČO</rsp:note>
            </rsp:responsePack>
            """);
        Assert.False(failed.Ok);
        Assert.Contains("Neplatné IČO", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_error_reads_note_attributes_and_import_details()
    {
        var failed = OrderPohodaXml.ReadImport("""
            <?xml version="1.0" encoding="Windows-1250"?>
            <rsp:responsePack version="2.0" id="order-Z2026-0007-v2" state="error" note="Import dokladu se nezdařil." xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd" xmlns:rdc="http://www.stormware.cz/schema/version_2/documentresponse.xsd" xmlns:ord="http://www.stormware.cz/schema/version_2/order.xsd">
              <rsp:responsePackItem version="2.0" id="Z2026-0007" state="error" note="XML není validní podle XSD schématu.">
                <ord:orderResponse version="2.0" state="error">
                  <rdc:importDetails>
                    <rdc:detail>
                      <rdc:state>error</rdc:state>
                      <rdc:errno>120</rdc:errno>
                      <rdc:note>Hodnota elementu je delší než povolených 90 znaků.</rdc:note>
                      <rdc:XPath>/dat:dataPack/dat:dataPackItem[1]/ord:order/ord:orderDetail/ord:orderItem[1]/ord:text</rdc:XPath>
                      <rdc:valueRequested>motorová nafta, 12000 l, Sklad Cerekvice</rdc:valueRequested>
                    </rdc:detail>
                    <rdc:detail>
                      <rdc:state>warning</rdc:state>
                      <rdc:errno>5</rdc:errno>
                      <rdc:note>Nebyla nalezena měrná jednotka, použije se výchozí.</rdc:note>
                      <rdc:XPath>/dat:dataPack/dat:dataPackItem[1]/ord:order/ord:orderDetail/ord:orderItem[1]/ord:unit</rdc:XPath>
                      <rdc:valueRequested>ks</rdc:valueRequested>
                      <rdc:valueProduced></rdc:valueProduced>
                    </rdc:detail>
                    <rdc:detail>
                      <rdc:state>ok</rdc:state>
                      <rdc:note>Tuto hlášku do chyby nedávat.</rdc:note>
                    </rdc:detail>
                  </rdc:importDetails>
                </ord:orderResponse>
              </rsp:responsePackItem>
            </rsp:responsePack>
            """);

        Assert.False(failed.Ok);
        var error = failed.Error ?? "";
        Assert.Contains("Import dokladu se nezdařil.", error, StringComparison.Ordinal);
        Assert.Contains("Z2026-0007: XML není validní podle XSD schématu.", error, StringComparison.Ordinal);
        Assert.Contains("[error] errno 120: Hodnota elementu je delší než povolených 90 znaků.", error, StringComparison.Ordinal);
        Assert.Contains("XPath=/dat:dataPack/dat:dataPackItem[1]/ord:order/ord:orderDetail/ord:orderItem[1]/ord:text", error, StringComparison.Ordinal);
        Assert.Contains("valueRequested=motorová nafta, 12000 l, Sklad Cerekvice", error, StringComparison.Ordinal);
        Assert.Contains("[warning] errno 5: Nebyla nalezena měrná jednotka, použije se výchozí.", error, StringComparison.Ordinal);
        Assert.Contains("XPath=/dat:dataPack/dat:dataPackItem[1]/ord:order/ord:orderDetail/ord:orderItem[1]/ord:unit", error, StringComparison.Ordinal);
        Assert.Contains("valueRequested=ks", error, StringComparison.Ordinal);
        Assert.DoesNotContain("valueProduced=", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuto hlášku do chyby nedávat.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Pohoda vrátila chybu při importu objednávky.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_error_without_a_note_falls_back_to_the_raw_body()
    {
        const string body = """
            <rsp:responsePack state="error" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd">
              <rsp:responsePackItem id="Z2026-0007" state="error"/>
            </rsp:responsePack>
            """;

        var failed = OrderPohodaXml.ReadImport(body);

        Assert.False(failed.Ok);
        Assert.Contains("Z2026-0007", failed.Error, StringComparison.Ordinal);
        Assert.Contains("responsePackItem", failed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Pohoda vrátila chybu při importu objednávky.", failed.Error, StringComparison.Ordinal);

        var invalid = OrderPohodaXml.ReadImport("tohle neni xml");
        Assert.Contains("mServer nevrátil platné XML.", invalid.Error, StringComparison.Ordinal);
        Assert.Contains("tohle neni xml", invalid.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_import_keeps_the_document_number_when_details_only_warn()
    {
        var ok = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="ok" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd" xmlns:rdc="http://www.stormware.cz/schema/version_2/documentresponse.xsd">
              <rsp:responsePackItem state="ok">
                <rdc:producedDetails>
                  <rdc:id>15</rdc:id>
                  <rdc:number>242100045</rdc:number>
                </rdc:producedDetails>
                <rdc:importDetails>
                  <rdc:detail>
                    <rdc:state>warning</rdc:state>
                    <rdc:errno>5</rdc:errno>
                    <rdc:note>Jen upozornění.</rdc:note>
                  </rdc:detail>
                </rdc:importDetails>
              </rsp:responsePackItem>
            </rsp:responsePack>
            """);

        Assert.True(ok.Ok);
        Assert.Equal(15, ok.Id);
        Assert.Equal("242100045", ok.Number);
    }

    [Fact]
    public void Create_xml_stays_inside_the_pohoda_order_schema()
    {
        var order = Sample(version: 2, price: 10000m, fuel: new string('F', 80)) with
        {
            Code = new string('K', 30),
            Status = OrderStatuses.KFakturaci,
            CustomerPohodaId = null,
            CustomerName = new string('C', 300),
            LoadingPlace = new string('N', 160),
            UnloadingPlace = new string('V', 160),
            Surcharges = 250m
        };
        var options = SampleOptions();
        options.Ico = "12345678901234567890";
        options.Application = new string('A', 120);
        var plan = new OrderSyncPlan(OrderSyncAction.Create, null, 2);
        var xml = OrderPohodaXml.Build(options, plan, order, lastSent: null);
        var document = XDocument.Parse(xml);
        var pack = document.Root!;

        Assert.Equal("2.0", pack.Attribute("version")?.Value);
        Assert.Equal("123456789012345", pack.Attribute("ico")?.Value);
        Assert.Equal(new string('A', 100), pack.Attribute("application")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(pack.Attribute("note")?.Value));
        Assert.InRange(pack.Attribute("id")?.Value.Length ?? 0, 1, 64);
        Assert.InRange(pack.Elements().First().Attribute("id")?.Value.Length ?? 0, 1, 64);

        var header = document.Descendants().First(element => element.Name.LocalName == "orderHeader");
        var orderElement = header.Parent!;
        Assert.Equal("2.0", orderElement.Attribute("version")?.Value);
        Assert.Equal("receivedOrder", header.Elements().First(element => element.Name.LocalName == "orderType").Value);
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", header.Elements().First(element => element.Name.LocalName == "date").Value);
        Assert.NotNull(header.Elements().First(element => element.Name.LocalName == "partnerIdentity"));
        Assert.Equal(255, header.Descendants().First(element => element.Name.LocalName == "company").Value.Length);
        Assert.InRange(header.Elements().First(element => element.Name.LocalName == "numberOrder").Value.Length, 1, 32);
        Assert.InRange(header.Elements().First(element => element.Name.LocalName == "text").Value.Length, 1, 240);
        Assert.DoesNotContain(document.Descendants().Where(element => element.Name.LocalName == "orderItem"), item =>
            item.Elements().First(element => element.Name.LocalName == "text").Value.Length > 90);

        var correction = order with { CurrentVersion = 12, PohodaOrderNumber = "242100045" };
        var correctionXml = OrderPohodaXml.Build(
            SampleOptions(),
            new OrderSyncPlan(OrderSyncAction.Correction, 2, 12),
            correction,
            order);
        var numberOrder = XDocument.Parse(correctionXml).Descendants().First(element => element.Name.LocalName == "numberOrder").Value;
        Assert.InRange(numberOrder.Length, 1, 32);
        Assert.StartsWith(new string('K', 30), numberOrder, StringComparison.Ordinal);
        Assert.EndsWith("/1", numberOrder, StringComparison.Ordinal);
        AssertOrderSchema(xml);
        AssertOrderSchema(correctionXml);
    }

    [Fact]
    public async Task Mark_failed_trims_last_error_to_the_queue_column()
    {
        var live = SampleOptions();
        live.SyncMode = "Live";
        var store = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(live));
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None));
        Assert.IsType<OrderMutation.Success>(await store.ConfirmAsync(created.Order.Id, CancellationToken.None));
        var entry = Assert.Single(await store.ListQueueAsync(CancellationToken.None));

        var boundary = new string('á', 999) + "\uD800\uDC00" + "konec";
        await store.MarkFailedAsync(entry.Id, boundary, CancellationToken.None);

        var stored = Assert.Single(await store.ListQueueAsync(CancellationToken.None));
        Assert.Equal(999, stored.LastError!.Length);
        Assert.Equal('á', stored.LastError[^1]);
        Assert.Equal(new string('á', 999), stored.LastError);
        Assert.Null(OrderSyncLimits.TrimToColumn("   ", OrderSyncLimits.LastErrorMaxLength));
        Assert.Equal(1000, OrderSyncLimits.TrimToColumn(new string('x', 1000), OrderSyncLimits.LastErrorMaxLength)!.Length);
        Assert.Equal(1000, OrderSyncLimits.TrimToColumn(new string('x', 1400), OrderSyncLimits.LastErrorMaxLength)!.Length);
    }

    [Fact]
    public void Live_is_the_default_and_write_credentials_fall_back()
    {
        var options = new PohodaOptions { Username = "cteni", Password = "heslo-cteni" };

        Assert.Equal("Live", options.EffectiveSyncMode());
        Assert.Equal(("cteni", "heslo-cteni"), options.ResolveWriteCredentials());

        options.SyncMode = "dryrun";
        Assert.Equal("DryRun", options.EffectiveSyncMode());
        options.SyncMode = "off";
        Assert.Equal("Off", options.EffectiveSyncMode());
        options.SyncMode = "";
        Assert.Equal("Live", options.EffectiveSyncMode());

        options.WriteUsername = "zapis";
        options.WritePassword = "";
        Assert.Equal(("cteni", "heslo-cteni"), options.ResolveWriteCredentials());

        options.WritePassword = "heslo-zapisu";
        Assert.Equal(("zapis", "heslo-zapisu"), options.ResolveWriteCredentials());
    }

    [Fact]
    public void Schema_creates_order_tables_without_dropping_existing_ones()
    {
        Assert.Contains("IF OBJECT_ID(N'dbo.Disp_Order', N'U') IS NULL", DispatchSchema.Script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_OrderVersion", DispatchSchema.Script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_SyncQueue", DispatchSchema.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", DispatchSchema.Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_flow_queues_create_update_and_correction_from_the_last_sent_version()
    {
        var live = SampleOptions();
        live.SyncMode = "Live";
        var store = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(live));
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None));
        Assert.Null(created.Order.LatestSyncStatus);

        var noted = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(created.Order.Id, Request(note: "jen poznámka", driverId: "drv-1", cmr: "CMR-1"), CancellationToken.None));
        Assert.Null(noted.Order.LatestSyncStatus);
        Assert.Equal(2, noted.Order.CurrentVersion);
        Assert.Empty(await store.ListQueueAsync(CancellationToken.None));

        var confirmed = Assert.IsType<OrderMutation.Success>(await store.ConfirmAsync(created.Order.Id, CancellationToken.None));
        var queue = await store.ListQueueAsync(CancellationToken.None);
        var create = Assert.Single(queue);
        Assert.Equal(OrderSyncActions.Create, create.Action);
        Assert.Equal(OrderQueueStatuses.Pending, create.Status);
        Assert.Equal(confirmed.Order.CurrentVersion, create.VersionTo);
        Assert.Contains("receivedOrder", create.PayloadXml, StringComparison.Ordinal);

        var repriced = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(created.Order.Id, Request(price: 12000m, note: "jen poznámka", driverId: "drv-1", cmr: "CMR-1", status: OrderStatuses.KFakturaci), CancellationToken.None));
        queue = await store.ListQueueAsync(CancellationToken.None);
        create = Assert.Single(queue);
        Assert.Equal(OrderSyncActions.Create, create.Action);
        Assert.Equal(repriced.Order.CurrentVersion, create.VersionTo);
        Assert.Contains("12000.00", create.PayloadXml, StringComparison.Ordinal);

        await store.MarkSentAsync(create, PohodaImportResult.Success(15, "242100045"), CancellationToken.None);
        var sent = (await store.GetAsync(created.Order.Id, CancellationToken.None))!.Order;
        Assert.Equal(15, sent.PohodaOrderId);
        Assert.Equal("242100045", sent.PohodaOrderNumber);
        Assert.Equal(repriced.Order.CurrentVersion, sent.LastSentVersion);

        var updated = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(created.Order.Id, Request(price: 13000m, note: "po odeslání", driverId: "drv-1", cmr: "CMR-1", status: OrderStatuses.KFakturaci), CancellationToken.None));
        queue = await store.ListQueueAsync(CancellationToken.None);
        Assert.Equal(2, queue.Count);
        var update = queue[0];
        Assert.Equal(OrderSyncActions.Update, update.Action);
        Assert.Equal(sent.LastSentVersion, update.VersionFrom);
        Assert.Contains(">15<", update.PayloadXml, StringComparison.Ordinal);
        Assert.Contains("ord:delete", update.PayloadXml, StringComparison.Ordinal);
        Assert.Contains("ord:add", update.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:update", update.PayloadXml, StringComparison.OrdinalIgnoreCase);
        await store.MarkSentAsync(update, PohodaImportResult.Success(21, "242100050"), CancellationToken.None);
        sent = (await store.GetAsync(created.Order.Id, CancellationToken.None))!.Order;
        Assert.Equal(21, sent.PohodaOrderId);
        Assert.Equal("242100050", sent.PohodaOrderNumber);

        var invoiced = Assert.IsType<OrderMutation.Success>(await store.MarkInvoicedAsync(created.Order.Id, "FV-1", CancellationToken.None));
        Assert.True(invoiced.Order.IsLocked);
        Assert.Equal(OrderStatuses.Vyfakturovano, invoiced.Order.Status);

        var corrected = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(
            created.Order.Id,
            Request(price: 15000m, surcharges: 500m, fuel: "benzin", note: "po faktuře", driverId: "drv-2", cmr: "CMR-2"),
            CancellationToken.None));
        queue = await store.ListQueueAsync(CancellationToken.None);
        var correction = queue[0];
        Assert.Equal(OrderSyncActions.Correction, correction.Action);
        Assert.Equal(updated.Order.CurrentVersion, correction.VersionFrom);
        Assert.Equal(OrderPohodaXml.LockedOrderWarning($"{created.Order.Code}/1"), correction.Warning);
        Assert.Contains($"{created.Order.Code}/1", correction.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:delete", correction.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain(">21<", correction.PayloadXml, StringComparison.Ordinal);
        var lines = OrderPohodaXml.Lines(OrderSyncAction.Correction, corrected.Order, (await store.GetAsync(created.Order.Id, CancellationToken.None))!
            .Versions.Single(version => version.CurrentVersion == correction.VersionFrom));
        Assert.Equal(2, lines.Count);
        Assert.Equal(-1m, lines[0].Quantity);
        Assert.Equal(OrderPohodaFields.Total(corrected.Order) - 13000m, lines[0].Amount + lines[1].Amount);
        Assert.Contains("motorová nafta", lines[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("Storno: ", lines[0].Text, StringComparison.Ordinal);
        Assert.Contains("benzin", lines[1].Text, StringComparison.Ordinal);
        Assert.True(corrected.Order.CurrentVersion > (correction.VersionFrom ?? 0));
        Assert.Equal(correction.Warning, corrected.Order.LatestSyncWarning);
    }

    [Fact]
    public void Import_reads_each_item_and_keeps_the_new_document_from_add()
    {
        var ok = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="ok" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd" xmlns:rdc="http://www.stormware.cz/schema/version_2/documentresponse.xsd">
              <rsp:responsePackItem id="Z2026-0001-del" state="ok">
                <rdc:producedDetails>
                  <rdc:id>15</rdc:id>
                  <rdc:number>242100045</rdc:number>
                </rdc:producedDetails>
              </rsp:responsePackItem>
              <rsp:responsePackItem id="Z2026-0001-add" state="ok">
                <rdc:producedDetails>
                  <rdc:id>99</rdc:id>
                  <rdc:number>242100099</rdc:number>
                </rdc:producedDetails>
              </rsp:responsePackItem>
            </rsp:responsePack>
            """);

        Assert.True(ok.Ok);
        Assert.False(ok.DeleteCommitted);
        Assert.Equal(99, ok.Id);
        Assert.Equal("242100099", ok.Number);

        var missing = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="error" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd" xmlns:rdc="http://www.stormware.cz/schema/version_2/documentresponse.xsd">
              <rsp:responsePackItem id="Z2026-0001-del" state="error" note="Záznam nebyl nalezen."/>
              <rsp:responsePackItem id="Z2026-0001-add" state="ok">
                <rdc:producedDetails>
                  <rdc:id>100</rdc:id>
                  <rdc:number>242100100</rdc:number>
                </rdc:producedDetails>
              </rsp:responsePackItem>
            </rsp:responsePack>
            """);
        Assert.True(missing.Ok);
        Assert.Equal(100, missing.Id);
        Assert.Equal("242100100", missing.Number);

        var partial = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="error" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd">
              <rsp:responsePackItem id="Z2026-0001-del" state="ok"/>
              <rsp:responsePackItem id="Z2026-0001-add" state="error" note="Číslo objednávky už existuje."/>
            </rsp:responsePack>
            """);
        Assert.False(partial.Ok);
        Assert.True(partial.DeleteCommitted);
        Assert.Contains("Smazání původní objednávky v Pohodě proběhlo", partial.Error, StringComparison.Ordinal);
        Assert.Contains("už doklad nemaže", partial.Error, StringComparison.Ordinal);
        Assert.Contains("Z2026-0001-add: Číslo objednávky už existuje.", partial.Error, StringComparison.Ordinal);

        var alreadyGone = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="error" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd">
              <rsp:responsePackItem id="Z2026-0001-del" state="error" note="Záznam nebyl nalezen."/>
              <rsp:responsePackItem id="Z2026-0001-add" state="error" note="Import selhal."/>
            </rsp:responsePack>
            """);
        Assert.False(alreadyGone.Ok);
        Assert.True(alreadyGone.DeleteCommitted);
        Assert.Contains("už neexistuje", alreadyGone.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_delete_retries_with_add_only_and_stores_the_new_id()
    {
        var live = SampleOptions();
        live.SyncMode = "Live";
        var store = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(live));
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(), CancellationToken.None));
        Assert.IsType<OrderMutation.Success>(await store.ConfirmAsync(created.Order.Id, CancellationToken.None));
        var create = Assert.Single(await store.ListQueueAsync(CancellationToken.None));
        await store.MarkSentAsync(create, PohodaImportResult.Success(15, "242100045"), CancellationToken.None);

        Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(
            created.Order.Id,
            Request(price: 13000m, status: OrderStatuses.KFakturaci),
            CancellationToken.None));
        var update = (await store.ListQueueAsync(CancellationToken.None))[0];
        Assert.Equal(OrderSyncActions.Update, update.Action);
        Assert.Contains("ord:delete", update.PayloadXml, StringComparison.Ordinal);

        var partial = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="error" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd">
              <rsp:responsePackItem id="Z2026-0001-del" state="ok"/>
              <rsp:responsePackItem id="Z2026-0001-add" state="error" note="mServer odmítl založení."/>
            </rsp:responsePack>
            """);
        Assert.True(partial.DeleteCommitted);
        var bundle = (await store.GetAsync(created.Order.Id, CancellationToken.None))!;
        var addOnly = OrderPohodaXml.AddOnly(live, update, bundle.Order, bundle.Versions.First(item => item.CurrentVersion == update.VersionFrom), 0);
        await store.MarkDeleteCommittedAsync(update, partial.Error!, addOnly, CancellationToken.None);

        var retry = (await store.ListQueueAsync(CancellationToken.None))[0];
        var after = (await store.GetAsync(created.Order.Id, CancellationToken.None))!.Order;
        Assert.Equal(OrderQueueStatuses.Pending, retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.Null(after.PohodaOrderId);
        Assert.Contains("už doklad nemaže", retry.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:delete", retry.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain(">15<", retry.PayloadXml, StringComparison.Ordinal);
        Assert.Contains("<ord:add", retry.PayloadXml, StringComparison.Ordinal);
        Assert.Equal(created.Order.Code, XDocument.Parse(retry.PayloadXml).Descendants().Single(element => element.Name.LocalName == "numberOrder").Value);
        Assert.Equal(1, XDocument.Parse(retry.PayloadXml).Descendants().Count(element => element.Name.LocalName == "dataPackItem"));
        Assert.Same(retry.PayloadXml, OrderPohodaXml.EnsureCurrent(live, retry, after, null, 0));
        AssertOrderSchema(retry.PayloadXml);

        var added = OrderPohodaXml.ReadImport("""
            <rsp:responsePack state="ok" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd" xmlns:rdc="http://www.stormware.cz/schema/version_2/documentresponse.xsd">
              <rsp:responsePackItem id="Z2026-0001-add" state="ok">
                <rdc:producedDetails>
                  <rdc:id>40</rdc:id>
                  <rdc:number>242100040</rdc:number>
                </rdc:producedDetails>
              </rsp:responsePackItem>
            </rsp:responsePack>
            """);
        Assert.True(added.Ok);
        Assert.Equal(40, added.Id);
        await store.MarkSentAsync(retry, added, CancellationToken.None);
        var stored = (await store.GetAsync(created.Order.Id, CancellationToken.None))!.Order;
        Assert.Equal(40, stored.PohodaOrderId);
        Assert.Equal("242100040", stored.PohodaOrderNumber);
        Assert.Equal(retry.VersionTo, stored.LastSentVersion);
    }

    [Fact]
    public async Task Locked_corrections_use_the_next_suffix_and_storno_the_last_sent_version()
    {
        var live = SampleOptions();
        live.SyncMode = "Live";
        var store = new MemoryOrderStore(new MemoryDispatchStore(), Options.Create(live));
        var created = Assert.IsType<OrderMutation.Success>(await store.CreateAsync(Request(price: 8000m), CancellationToken.None));
        Assert.IsType<OrderMutation.Success>(await store.ConfirmAsync(created.Order.Id, CancellationToken.None));
        var create = Assert.Single(await store.ListQueueAsync(CancellationToken.None));
        await store.MarkSentAsync(create, PohodaImportResult.Success(15, "242100045"), CancellationToken.None);
        Assert.IsType<OrderMutation.Success>(await store.MarkInvoicedAsync(created.Order.Id, "FV-9", CancellationToken.None));

        var first = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(
            created.Order.Id,
            Request(price: 10000m, surcharges: 500m, fuel: "benzin"),
            CancellationToken.None));
        var correction = (await store.ListQueueAsync(CancellationToken.None))[0];
        Assert.Equal(OrderSyncActions.Correction, correction.Action);
        Assert.Equal($"{created.Order.Code}/1", NumberOrder(correction.PayloadXml));
        Assert.Equal(OrderPohodaXml.LockedOrderWarning($"{created.Order.Code}/1"), correction.Warning);
        Assert.Equal(correction.Warning, first.Order.LatestSyncWarning);
        var firstLines = Items(correction.PayloadXml);
        Assert.Equal(2, firstLines.Count);
        Assert.Equal("-1", firstLines[0].Quantity);
        Assert.StartsWith("Storno: ", firstLines[0].Text, StringComparison.Ordinal);
        Assert.Contains("motorová nafta", firstLines[0].Text, StringComparison.Ordinal);
        Assert.Equal("8000.00", firstLines[0].Price);
        Assert.Contains("benzin", firstLines[1].Text, StringComparison.Ordinal);
        Assert.Equal("10500.00", firstLines[1].Price);
        Assert.DoesNotContain("ord:delete", correction.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain(">15<", correction.PayloadXml, StringComparison.Ordinal);
        AssertOrderSchema(correction.PayloadXml);

        await store.MarkSentAsync(correction, PohodaImportResult.Success(30, "242100060"), CancellationToken.None);
        var linked = (await store.GetAsync(created.Order.Id, CancellationToken.None))!.Order;
        Assert.Equal(30, linked.PohodaOrderId);
        Assert.Equal("242100060", linked.PohodaOrderNumber);

        var second = Assert.IsType<OrderMutation.Success>(await store.UpdateAsync(
            created.Order.Id,
            Request(price: 16000m, fuel: "benzin"),
            CancellationToken.None));
        var next = (await store.ListQueueAsync(CancellationToken.None))[0];
        Assert.Equal($"{created.Order.Code}/2", NumberOrder(next.PayloadXml));
        Assert.Equal(OrderPohodaXml.LockedOrderWarning($"{created.Order.Code}/2"), next.Warning);
        Assert.Equal(next.Warning, second.Order.LatestSyncWarning);
        var secondLines = Items(next.PayloadXml);
        Assert.Equal(2, secondLines.Count);
        Assert.Equal("-1", secondLines[0].Quantity);
        Assert.StartsWith("Storno: ", secondLines[0].Text, StringComparison.Ordinal);
        Assert.Contains("benzin", secondLines[0].Text, StringComparison.Ordinal);
        Assert.Equal("10500.00", secondLines[0].Price);
        Assert.DoesNotContain("motorová nafta", secondLines[0].Text, StringComparison.Ordinal);
        Assert.Equal("16000.00", secondLines[1].Price);
        Assert.DoesNotContain(">30<", next.PayloadXml, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:delete", next.PayloadXml, StringComparison.Ordinal);
        AssertOrderSchema(next.PayloadXml);
        Assert.Equal(32, OrderPohodaXml.CorrectionNumber(new string('K', 30), 10).Length);
        Assert.EndsWith("/10", OrderPohodaXml.CorrectionNumber(new string('K', 30), 10), StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_update_xml_is_rebuilt_as_delete_and_add()
    {
        const string legacy = """
            <?xml version="1.0" encoding="Windows-1250"?>
            <dat:dataPack version="2.0" id="order-Z2026-0001-v4" ico="12345678" application="PHMTRANS" note="Zakázka dispečinku" xmlns:dat="http://www.stormware.cz/schema/version_2/data.xsd" xmlns:ord="http://www.stormware.cz/schema/version_2/order.xsd" xmlns:typ="http://www.stormware.cz/schema/version_2/type.xsd" xmlns:ftr="http://www.stormware.cz/schema/version_2/filter.xsd">
              <dat:dataPackItem version="2.0" id="Z2026-0001">
                <ord:order version="2.0">
                  <ord:actionType>
                    <ord:update>
                      <ftr:filter><ftr:id>15</ftr:id></ftr:filter>
                    </ord:update>
                  </ord:actionType>
                </ord:order>
              </dat:dataPackItem>
            </dat:dataPack>
            """;
        var order = Sent(version: 4, price: 13000m, surcharges: 0m, fuel: "motorová nafta");
        var entry = new OrderSyncEntry(8, order.Id, order.Code, 2, 4, OrderSyncActions.Update, legacy, OrderQueueStatuses.Pending, 1, "ord:update není ve schématu", DateTimeOffset.Parse("2026-11-04T08:00:00Z"), DateTimeOffset.Parse("2026-11-04T08:01:00Z"), null);
        var rebuilt = OrderPohodaXml.EnsureCurrent(SampleOptions(), entry, order, order, 0);
        var document = XDocument.Parse(rebuilt);
        var items = document.Descendants().Where(element => element.Name.LocalName == "dataPackItem").ToList();

        Assert.NotEqual(legacy, rebuilt);
        Assert.Equal(2, items.Count);
        Assert.Equal("15", items[0].Descendants().First(element => element.Name.LocalName == "id").Value);
        Assert.Equal(order.Code, items[1].Descendants().First(element => element.Name.LocalName == "numberOrder").Value);
        Assert.Contains("13000.00", rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("ord:update", rebuilt, StringComparison.OrdinalIgnoreCase);
        AssertOrderSchema(rebuilt);
        Assert.Equal(rebuilt, OrderPohodaXml.EnsureCurrent(SampleOptions(), entry with { PayloadXml = rebuilt }, order, order, 0));
    }

    private static DispatchOrder Sample(int version, decimal price, string fuel) => new(
        "ord-1",
        "Z2026-0001",
        null,
        "Velkoobchod Demo s.r.o.",
        "Sklad Cerekvice",
        "ČS Praha",
        Loading,
        Unloading,
        fuel,
        12000m,
        price,
        0m,
        null,
        null,
        null,
        null,
        null,
        OrderStatuses.Nova,
        false,
        version,
        null,
        null,
        null,
        null,
        DateTimeOffset.Parse("2026-11-03T08:00:00+01:00"),
        DateTimeOffset.Parse("2026-11-03T08:00:00+01:00"));

    private static DispatchOrder Sent(int version, decimal price, decimal surcharges, string fuel) =>
        Sample(version, price, fuel) with
        {
            Surcharges = surcharges,
            Status = OrderStatuses.KFakturaci,
            PohodaOrderId = 15,
            PohodaOrderNumber = "242100045",
            LastSentVersion = version
        };

    private static PohodaOptions SampleOptions() => new()
    {
        Ico = "12345678",
        Application = "PHMTRANS",
        SyncMode = "DryRun",
        Username = "cteni",
        Password = "heslo"
    };

    private static OrderSaveRequest Request(
        decimal price = 10000m,
        decimal surcharges = 0m,
        string fuel = "motorová nafta",
        string? note = null,
        string? driverId = null,
        string? cmr = null,
        string? status = null) =>
        new(
            42,
            "Velkoobchod Demo s.r.o.",
            "Sklad Cerekvice",
            "ČS Praha",
            Loading,
            Unloading,
            fuel,
            12000m,
            price,
            surcharges,
            note,
            cmr,
            null,
            driverId,
            null,
            status ?? OrderStatuses.Nova,
            null);

    private static string Value(XDocument document, string parent, string child) =>
        document.Descendants().First(element => element.Name.LocalName == parent)
            .Descendants().First(element => element.Name.LocalName == child).Value;

    private static IReadOnlyList<decimal> Prices(XDocument document) =>
        document.Descendants()
            .Where(element => element.Name.LocalName == "unitPrice")
            .Select(element => decimal.Parse(element.Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

    private static string NumberOrder(string xml) =>
        XDocument.Parse(xml).Descendants().Single(element => element.Name.LocalName == "numberOrder").Value;

    private static IReadOnlyList<(string Text, string Quantity, string Price)> Items(string xml) =>
        XDocument.Parse(xml).Descendants()
            .Where(element => element.Name.LocalName == "orderItem")
            .Select(element => (
                element.Elements().First(child => child.Name.LocalName == "text").Value,
                element.Elements().First(child => child.Name.LocalName == "quantity").Value,
                element.Descendants().First(child => child.Name.LocalName == "unitPrice").Value))
            .ToArray();

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
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Distinct().Take(20)));
        return set;
    }

    private static void AssertOrderSchema(string xml)
    {
        var orders = XDocument.Parse(xml).Descendants()
            .Where(element => element.Name.LocalName == "order")
            .ToList();
        Assert.NotEmpty(orders);
        foreach (var order in orders)
        {
            var errors = new List<string>();
            var document = new XDocument(new XElement(order));
            document.Validate(OrderSchema, (_, args) => errors.Add(args.Message), true);
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }
    }
}
