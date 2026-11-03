using System.Globalization;
using System.Xml.Linq;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Dispatch;

public sealed record OrderXmlLine(string Text, decimal Quantity, decimal UnitPrice)
{
    public decimal Amount => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

public static class OrderPohodaXml
{
    private static readonly XNamespace Dat = "http://www.stormware.cz/schema/version_2/data.xsd";
    private static readonly XNamespace Ord = "http://www.stormware.cz/schema/version_2/order.xsd";
    private static readonly XNamespace Typ = "http://www.stormware.cz/schema/version_2/type.xsd";
    private static readonly XNamespace Ftr = "http://www.stormware.cz/schema/version_2/filter.xsd";

    public static IReadOnlyList<OrderXmlLine> Lines(OrderSyncAction action, DispatchOrder current, DispatchOrder? lastSent)
    {
        if (action == OrderSyncAction.Correction)
        {
            if (lastSent is null)
                throw new InvalidOperationException("Oprava potřebuje poslední odeslanou verzi.");

            return
            [
                new OrderXmlLine("Storno " + ItemText(lastSent), 1m, -OrderPohodaFields.Total(lastSent)),
                new OrderXmlLine(ItemText(current), 1m, OrderPohodaFields.Total(current))
            ];
        }

        var lines = new List<OrderXmlLine>
        {
            new(ItemText(current), 1m, OrderPohodaFields.Money(current.AgreedPrice))
        };
        if (current.Surcharges != 0)
            lines.Add(new OrderXmlLine("Příplatky", 1m, OrderPohodaFields.Money(current.Surcharges)));
        return lines;
    }

    public static string Build(PohodaOptions options, OrderSyncPlan plan, DispatchOrder current, DispatchOrder? lastSent)
    {
        if (plan.Action is OrderSyncAction.None)
            throw new InvalidOperationException("Prázdný plán se do XML nepřevádí.");

        var lines = Lines(plan.Action, current, lastSent);
        var packId = $"order-{current.Code}-v{current.CurrentVersion}";
        var header = new XElement(Ord + "orderHeader",
            new XElement(Ord + "orderType", "receivedOrder"),
            new XElement(Ord + "numberOrder", ExternalNumber(plan, current)),
            new XElement(Ord + "date", Date(current.LoadingDate)),
            new XElement(Ord + "dateFrom", Date(current.LoadingDate)),
            new XElement(Ord + "dateTo", Date(current.UnloadingDate)),
            new XElement(Ord + "text", Trim(DocumentText(plan, current, lastSent), 240)),
            Partner(current),
            new XElement(Ord + "intNote", Trim($"Dispečink {current.Code}, verze {current.CurrentVersion}", 240)));

        var detail = new XElement(Ord + "orderDetail", lines.Select(Item));
        var summary = new XElement(Ord + "orderSummary",
            new XElement(Ord + "roundingDocument", "none"));

        var order = new XElement(Ord + "order", new XAttribute("version", "2.0"));
        if (plan.Action == OrderSyncAction.Update)
        {
            if (current.PohodaOrderId is not int id)
                throw new InvalidOperationException("Aktualizace potřebuje id dokladu v Pohodě.");

            order.Add(new XElement(Ord + "actionType",
                new XElement(Ord + "update",
                    new XElement(Ftr + "filter",
                        new XElement(Ftr + "id", id.ToString(CultureInfo.InvariantCulture))))));
        }

        order.Add(header, detail, summary);

        var document = new XElement(Dat + "dataPack",
            new XAttribute("version", "2.0"),
            new XAttribute("id", packId),
            new XAttribute("ico", options.Ico ?? ""),
            new XAttribute("application", string.IsNullOrWhiteSpace(options.Application) ? "PHMTRANS" : options.Application),
            new XAttribute("note", "Zakázka dispečinku"),
            new XAttribute(XNamespace.Xmlns + "dat", Dat),
            new XAttribute(XNamespace.Xmlns + "ord", Ord),
            new XAttribute(XNamespace.Xmlns + "typ", Typ),
            new XAttribute(XNamespace.Xmlns + "ftr", Ftr),
            new XElement(Dat + "dataPackItem",
                new XAttribute("version", "2.0"),
                new XAttribute("id", current.Code),
                order));

        return $"<?xml version=\"1.0\" encoding=\"Windows-1250\"?>{document.ToString(SaveOptions.DisableFormatting)}";
    }

    public static PohodaImportResult ReadImport(string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException)
        {
            return PohodaImportResult.Failure("mServer nevrátil platné XML.");
        }

        var packState = State(document, "responsePack");
        var itemState = State(document, "responsePackItem");
        if (IsError(packState) || IsError(itemState))
        {
            var note = First(document, "note");
            if (note.Length == 0)
                note = First(document, "message");
            return PohodaImportResult.Failure(note.Length == 0 ? "Pohoda vrátila chybu při importu objednávky." : note);
        }

        var produced = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "producedDetails");
        var idText = produced?.Elements().FirstOrDefault(element => element.Name.LocalName == "id")?.Value.Trim() ?? "";
        var number = produced?.Elements().FirstOrDefault(element => element.Name.LocalName == "number")?.Value.Trim() ?? "";
        int? id = int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        if (id is null && number.Length == 0)
            return PohodaImportResult.Failure("Pohoda nevrátila číslo dokladu.");

        return PohodaImportResult.Success(id, number.Length == 0 ? null : number);
    }

    private static string ItemText(DispatchOrder order) =>
        Trim($"{order.FuelType}, {Liters(order.Liters)} l, {order.LoadingPlace} → {order.UnloadingPlace}", 240);

    private static string DocumentText(OrderSyncPlan plan, DispatchOrder current, DispatchOrder? lastSent)
    {
        if (plan.Action == OrderSyncAction.Correction)
        {
            var number = string.IsNullOrWhiteSpace(current.PohodaOrderNumber) ? current.Code : current.PohodaOrderNumber;
            var from = lastSent?.CurrentVersion ?? plan.VersionFrom;
            return $"Oprava k {number}: {current.Code} verze {from}–{current.CurrentVersion}";
        }

        return $"{current.Code}: {current.LoadingPlace} → {current.UnloadingPlace}, {current.FuelType}";
    }

    private static string ExternalNumber(OrderSyncPlan plan, DispatchOrder current) =>
        plan.Action == OrderSyncAction.Correction
            ? Trim($"{current.Code}/v{current.CurrentVersion}", 40)
            : current.Code;

    private static XElement Partner(DispatchOrder order)
    {
        if (order.CustomerPohodaId is int id)
        {
            return new XElement(Ord + "partnerIdentity",
                new XElement(Typ + "id", id.ToString(CultureInfo.InvariantCulture)));
        }

        return new XElement(Ord + "partnerIdentity",
            new XElement(Typ + "address",
                new XElement(Typ + "company", order.CustomerName)));
    }

    private static XElement Item(OrderXmlLine line) =>
        new(Ord + "orderItem",
            new XElement(Ord + "text", line.Text),
            new XElement(Ord + "quantity", line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)),
            new XElement(Ord + "unit", "ks"),
            new XElement(Ord + "payVAT", "false"),
            // Sazbu DPH teď neřešíme. none = položka bez rozlišení daně.
            new XElement(Ord + "rateVAT", "none"),
            new XElement(Ord + "homeCurrency",
                new XElement(Typ + "unitPrice", line.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture))));

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Liters(decimal liters) => liters.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string State(XDocument document, string localName) =>
        document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Attribute("state")?.Value ?? "";

    private static bool IsError(string state) =>
        state.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static string First(XDocument document, string localName) =>
        document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim() ?? "";
}
