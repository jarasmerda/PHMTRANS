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

    // Délky z order.xsd / type.xsd / data.xsd, verze 2.0.
    private const int ItemTextMax = 90;
    private const int HeaderTextMax = 240;
    private const int NumberOrderMax = 32;
    private const int PackIdMax = 64;
    private const int CompanyMax = 255;
    private const int IcoMax = 15;
    private const int ApplicationMax = 100;
    private const int RawFallbackLimit = 2000;

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
        var packId = Trim($"order-{current.Code}-v{current.CurrentVersion}", PackIdMax);
        var header = new XElement(Ord + "orderHeader",
            new XElement(Ord + "orderType", "receivedOrder"),
            new XElement(Ord + "numberOrder", ExternalNumber(plan, current)),
            new XElement(Ord + "date", Date(current.LoadingDate)),
            new XElement(Ord + "dateFrom", Date(current.LoadingDate)),
            new XElement(Ord + "dateTo", Date(current.UnloadingDate)),
            new XElement(Ord + "text", Trim(DocumentText(plan, current, lastSent), HeaderTextMax)),
            Partner(current),
            new XElement(Ord + "intNote", Trim($"Dispečink {current.Code}, verze {current.CurrentVersion}", HeaderTextMax)));

        var detail = new XElement(Ord + "orderDetail", lines.Select(Item));
        var summary = new XElement(Ord + "orderSummary",
            new XElement(Ord + "roundingDocument", "none"));

        var order = new XElement(Ord + "order", new XAttribute("version", "2.0"));
        if (plan.Action == OrderSyncAction.Update)
        {
            if (current.PohodaOrderId is not int id)
                throw new InvalidOperationException("Aktualizace potřebuje id dokladu v Pohodě.");

            // Veřejné order.xsd 2.0 má v actionType jen add a delete; update je zakomentovaný.
            // Filtr odpovídá tomu zakomentovanému ftr:requestOrderType. Když ho Pohoda odmítne,
            // text chyby se uloží do LastError.
            order.Add(new XElement(Ord + "actionType",
                new XElement(Ord + "update",
                    new XElement(Ftr + "filter",
                        new XElement(Ftr + "id", id.ToString(CultureInfo.InvariantCulture))))));
        }

        order.Add(header, detail, summary);

        var document = new XElement(Dat + "dataPack",
            new XAttribute("version", "2.0"),
            new XAttribute("id", packId),
            new XAttribute("ico", Trim((options.Ico ?? "").Trim(), IcoMax)),
            new XAttribute("application", Trim(string.IsNullOrWhiteSpace(options.Application) ? "PHMTRANS" : options.Application.Trim(), ApplicationMax)),
            new XAttribute("note", "Zakázka dispečinku"),
            new XAttribute(XNamespace.Xmlns + "dat", Dat),
            new XAttribute(XNamespace.Xmlns + "ord", Ord),
            new XAttribute(XNamespace.Xmlns + "typ", Typ),
            new XAttribute(XNamespace.Xmlns + "ftr", Ftr),
            new XElement(Dat + "dataPackItem",
                new XAttribute("version", "2.0"),
                new XAttribute("id", Trim(current.Code, PackIdMax)),
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
            var raw = OrderSyncLimits.Cut(body.Trim(), RawFallbackLimit);
            return PohodaImportResult.Failure(raw.Length == 0
                ? "mServer nevrátil platné XML."
                : $"mServer nevrátil platné XML. {raw}");
        }

        var packState = State(document, "responsePack");
        var itemState = State(document, "responsePackItem");
        if (IsError(packState) || IsError(itemState))
            return PohodaImportResult.Failure(DescribeImportError(document, body));

        var produced = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "producedDetails");
        var idText = produced?.Elements().FirstOrDefault(element => element.Name.LocalName == "id")?.Value.Trim() ?? "";
        var number = produced?.Elements().FirstOrDefault(element => element.Name.LocalName == "number")?.Value.Trim() ?? "";
        int? id = int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        if (id is null && number.Length == 0)
            return PohodaImportResult.Failure("Pohoda nevrátila číslo dokladu.");

        return PohodaImportResult.Success(id, number.Length == 0 ? null : number);
    }

    private static string DescribeImportError(XDocument document, string body)
    {
        var parts = new List<string>();
        foreach (var pack in document.Descendants().Where(element => element.Name.LocalName == "responsePack"))
            Add(parts, Attribute(pack, "note"));

        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "responsePackItem"))
        {
            var note = Attribute(item, "note");
            if (note.Length == 0)
                continue;

            var id = Attribute(item, "id");
            Add(parts, id.Length == 0 ? note : $"{id}: {note}");
        }

        foreach (var element in document.Descendants().Where(element =>
                     element.Name.LocalName.Equals("note", StringComparison.OrdinalIgnoreCase)
                     || element.Name.LocalName.Equals("message", StringComparison.OrdinalIgnoreCase)))
        {
            if (element.Ancestors().Any(ancestor => ancestor.Name.LocalName.Equals("detail", StringComparison.OrdinalIgnoreCase)))
                continue;

            Add(parts, element.Value.Trim());
        }

        foreach (var detail in document.Descendants().Where(element => element.Name.LocalName.Equals("detail", StringComparison.OrdinalIgnoreCase)))
        {
            var state = Child(detail, "state");
            if (!state.Equals("error", StringComparison.OrdinalIgnoreCase)
                && !state.Equals("warning", StringComparison.OrdinalIgnoreCase))
                continue;

            var errno = Child(detail, "errno");
            var note = Child(detail, "note");
            var xpath = Child(detail, "XPath");
            var requested = Child(detail, "valueRequested");
            var produced = Child(detail, "valueProduced");
            if (errno.Length == 0 && note.Length == 0 && xpath.Length == 0 && requested.Length == 0 && produced.Length == 0)
                continue;

            var text = $"[{state.ToLowerInvariant()}]";
            if (errno.Length > 0)
                text += $" errno {errno}";
            if (note.Length > 0)
                text += $": {note}";
            if (xpath.Length > 0)
                text += $" XPath={xpath}";
            if (requested.Length > 0)
                text += $" valueRequested={requested}";
            if (produced.Length > 0)
                text += $" valueProduced={produced}";
            Add(parts, text);
        }

        if (parts.Count == 0)
        {
            var raw = OrderSyncLimits.Cut(body.Trim(), RawFallbackLimit);
            return raw.Length == 0 ? "Pohoda vrátila chybu při importu objednávky." : raw;
        }

        return string.Join(" | ", parts);
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
            ? Trim($"{current.Code}/v{current.CurrentVersion}", NumberOrderMax)
            : Trim(current.Code, NumberOrderMax);

    private static XElement Partner(DispatchOrder order)
    {
        if (order.CustomerPohodaId is int id && id >= 1)
        {
            return new XElement(Ord + "partnerIdentity",
                new XElement(Typ + "id", id.ToString(CultureInfo.InvariantCulture)));
        }

        return new XElement(Ord + "partnerIdentity",
            new XElement(Typ + "address",
                new XElement(Typ + "company", Trim(order.CustomerName, CompanyMax))));
    }

    private static XElement Item(OrderXmlLine line) =>
        new(Ord + "orderItem",
            new XElement(Ord + "text", Trim(line.Text, ItemTextMax)),
            new XElement(Ord + "quantity", line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)),
            new XElement(Ord + "unit", "ks"),
            new XElement(Ord + "payVAT", "false"),
            // Sazbu DPH teď neřešíme. none = položka bez rozlišení daně.
            new XElement(Ord + "rateVAT", "none"),
            new XElement(Ord + "homeCurrency",
                new XElement(Typ + "unitPrice", line.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture))));

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Liters(decimal liters) => liters.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Trim(string value, int max) => OrderSyncLimits.Cut(value, max);

    private static string State(XDocument document, string localName)
    {
        var state = "";
        foreach (var element in document.Descendants().Where(element => element.Name.LocalName == localName))
        {
            var value = Attribute(element, "state");
            if (IsError(value))
                return value;
            if (state.Length == 0)
                state = value;
        }

        return state;
    }

    private static bool IsError(string state) =>
        state.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static string Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";

    private static string Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";

    private static void Add(List<string> parts, string value)
    {
        if (value.Length == 0 || parts.Contains(value))
            return;

        parts.Add(value);
    }
}
