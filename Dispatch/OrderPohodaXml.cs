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

    public const string DeleteItemSuffix = "-del";
    public const string AddItemSuffix = "-add";
    public const string ReceivedOrderAgenda = "prijate_objednavky";

    public static string LockedOrderWarning(string numberOrder) =>
        $"Původní objednávku nelze změnit, protože je uzamčena (např. vyfakturována). Vytvořena opravná objednávka {numberOrder}.";

    public static string CorrectionNumber(string code, int index)
    {
        var safeIndex = index < 1 ? 1 : index;
        var suffix = "/" + safeIndex.ToString(CultureInfo.InvariantCulture);
        var room = NumberOrderMax - suffix.Length;
        if (room < 1)
            return Trim(suffix, NumberOrderMax);

        var baseCode = string.IsNullOrWhiteSpace(code) ? "Z" : code.Trim();
        if (baseCode.Length > room)
            baseCode = baseCode[..room];
        return baseCode + suffix;
    }

    public static IReadOnlyList<OrderXmlLine> Lines(OrderSyncAction action, DispatchOrder current, DispatchOrder? lastSent)
    {
        if (action is OrderSyncAction.Correction or OrderSyncAction.Cancel)
        {
            if (lastSent is null)
                throw new InvalidOperationException("Oprava potřebuje poslední odeslanou verzi.");

            var lines = new List<OrderXmlLine>();
            foreach (var line in SaleItems(lastSent))
                lines.Add(new OrderXmlLine("Storno: " + line.Text, -line.Quantity, line.UnitPrice));
            if (action == OrderSyncAction.Correction)
                lines.AddRange(SaleItems(current));
            return lines;
        }

        return SaleItems(current);
    }

    private static IReadOnlyList<OrderXmlLine> SaleItems(DispatchOrder order)
    {
        var lines = OrderLegItems.SaleLines(order).ToList();
        if (order.Surcharges != 0)
            lines.Add(new OrderXmlLine("Příplatky", 1m, OrderPohodaFields.Money(order.Surcharges)));
        return lines;
    }

    public static string Build(PohodaOptions options, OrderSyncPlan plan, DispatchOrder current, DispatchOrder? lastSent)
    {
        if (plan.Action is OrderSyncAction.None)
            throw new InvalidOperationException("Prázdný plán se do XML nepřevádí.");

        var items = new List<XElement>();
        if (plan.Action == OrderSyncAction.Delete || (plan.Action == OrderSyncAction.Update && plan.IncludeDelete))
        {
            if (current.PohodaOrderId is not int id || id < 1)
                throw new InvalidOperationException(plan.Action == OrderSyncAction.Delete
                    ? "Smazání potřebuje id dokladu v Pohodě."
                    : "Aktualizace potřebuje id dokladu v Pohodě.");

            items.Add(PackItem(ItemId(current.Code, DeleteItemSuffix), DeleteOrder(id)));
        }

        if (plan.Action == OrderSyncAction.Delete)
        {
            return Pack(options, current, items);
        }

        var explicitAdd = plan.Action is OrderSyncAction.Update or OrderSyncAction.Correction or OrderSyncAction.Cancel;
        var addSuffix = plan.Action == OrderSyncAction.Update ? AddItemSuffix : "";
        items.Add(PackItem(ItemId(current.Code, addSuffix), DocumentOrder(plan, current, lastSent, explicitAdd)));

        return Pack(options, current, items);
    }

    private static string Pack(PohodaOptions options, DispatchOrder current, IReadOnlyList<XElement> items)
    {
        var packId = Trim($"order-{current.Code}-v{current.CurrentVersion}", PackIdMax);
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
            items);

        return $"<?xml version=\"1.0\" encoding=\"Windows-1250\"?>{document.ToString(SaveOptions.DisableFormatting)}";
    }

    public static bool ShouldRebuild(string payload, int? pohodaOrderId)
    {
        if (ContainsRejectedUpdate(payload))
            return true;

        var deleteId = ReadDeleteId(payload);
        return deleteId is not null && deleteId != pohodaOrderId;
    }

    public static string EnsureCurrent(
        PohodaOptions options,
        OrderSyncEntry entry,
        DispatchOrder current,
        DispatchOrder? lastSent,
        int sentCorrections)
    {
        if (!ShouldRebuild(entry.PayloadXml, current.PohodaOrderId))
            return entry.PayloadXml;

        return Build(options, PlanFor(entry, current, sentCorrections, current.PohodaOrderId is > 0), current, lastSent);
    }

    public static string AddOnly(
        PohodaOptions options,
        OrderSyncEntry entry,
        DispatchOrder current,
        DispatchOrder? lastSent,
        int sentCorrections) =>
        Build(
            options,
            PlanFor(entry, current, sentCorrections, includeDelete: false),
            current with { PohodaOrderId = null },
            lastSent);

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

        var items = document.Descendants().Where(element => element.Name.LocalName == "responsePackItem").ToList();
        var deletes = items.Where(IsDeleteResponse).ToList();
        var adds = items.Where(element => !IsDeleteResponse(element)).ToList();
        if (deletes.Count > 0 && adds.Count == 0)
        {
            return deletes.All(IsAcceptableDelete)
                ? PohodaImportResult.Success(null, null)
                : PohodaImportResult.Failure(DescribeImportError(document, body));
        }

        if (deletes.Count == 0)
            return ReadSingle(document, body, adds.Count == 1 ? adds[0] : null);

        var deleteOk = deletes.All(IsAcceptableDelete);
        var addOk = adds.Count > 0 && adds.All(element => !IsItemError(element));
        if (deleteOk && addOk)
        {
            var produced = Produced(adds[^1]);
            if (produced.Id is null && produced.Number.Length == 0)
                return PohodaImportResult.Failure("Pohoda nevrátila číslo dokladu.", deleteCommitted: true);

            return PohodaImportResult.Success(produced.Id, produced.Number.Length == 0 ? null : produced.Number);
        }

        var detail = DescribeImportError(document, body);
        if (!deleteOk)
            return PohodaImportResult.Failure(detail);

        var gone = deletes.Any(IsItemError);
        var message = gone
            ? "Původní objednávka v Pohodě už neexistuje. Novou verzi se nepodařilo založit. Další pokus už doklad nemaže a jen ho znovu založí. "
            : "Smazání původní objednávky v Pohodě proběhlo, ale novou verzi se nepodařilo založit. Další pokus už doklad nemaže a jen ho znovu založí. ";
        return PohodaImportResult.Failure(message + detail, deleteCommitted: true);
    }

    private static OrderSyncPlan PlanFor(OrderSyncEntry entry, DispatchOrder current, int sentCorrections, bool includeDelete)
    {
        var action = entry.Action switch
        {
            OrderSyncActions.Create => OrderSyncAction.Create,
            OrderSyncActions.Correction => OrderSyncAction.Correction,
            OrderSyncActions.Delete => OrderSyncAction.Delete,
            OrderSyncActions.Cancel => OrderSyncAction.Cancel,
            _ => OrderSyncAction.Update
        };
        var index = action is OrderSyncAction.Correction or OrderSyncAction.Cancel ? sentCorrections + 1 : 1;
        var delete = action == OrderSyncAction.Delete || (action == OrderSyncAction.Update && includeDelete);
        return new OrderSyncPlan(action, entry.VersionFrom, current.CurrentVersion, index < 1 ? 1 : index, delete);
    }

    private static PohodaImportResult ReadSingle(XDocument document, string body, XElement? item)
    {
        var packState = State(document, "responsePack");
        var itemState = item is null ? State(document, "responsePackItem") : Attribute(item, "state");
        if (IsError(packState) || IsError(itemState))
            return PohodaImportResult.Failure(DescribeImportError(document, body));

        var produced = item is null ? Produced(document.Root ?? new XElement("empty")) : Produced(item);
        if (produced.Id is null && produced.Number.Length == 0)
            return PohodaImportResult.Failure("Pohoda nevrátila číslo dokladu.");

        return PohodaImportResult.Success(produced.Id, produced.Number.Length == 0 ? null : produced.Number);
    }

    private static bool ContainsRejectedUpdate(string payload)
    {
        try
        {
            var document = XDocument.Parse(payload);
            return document.Descendants().Any(element =>
                element.Name.LocalName.Equals("update", StringComparison.OrdinalIgnoreCase)
                && element.Parent is not null
                && element.Parent.Name.LocalName.Equals("actionType", StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Xml.XmlException)
        {
            return payload.Contains(":update", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int? ReadDeleteId(string payload)
    {
        try
        {
            var document = XDocument.Parse(payload);
            var delete = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("delete", StringComparison.OrdinalIgnoreCase));
            var idText = delete?.Descendants().FirstOrDefault(element => element.Name.LocalName == "id")?.Value.Trim() ?? "";
            return int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool IsDeleteResponse(XElement item) =>
        Attribute(item, "id").EndsWith(DeleteItemSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool IsItemError(XElement item) =>
        Attribute(item, "state").Equals("error", StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceptableDelete(XElement item) =>
        !IsItemError(item) || IsMissingRecord(item);

    private static bool IsMissingRecord(XElement item)
    {
        var text = string.Join(' ', item.Attributes().Select(attribute => attribute.Value)
            .Concat(item.DescendantNodes().OfType<XText>().Select(node => node.Value)));
        return text.Contains("nenalezen", StringComparison.OrdinalIgnoreCase)
            || text.Contains("nebyl nalezen", StringComparison.OrdinalIgnoreCase)
            || text.Contains("nebyla nalezena", StringComparison.OrdinalIgnoreCase)
            || text.Contains("nebylo nalezeno", StringComparison.OrdinalIgnoreCase)
            || text.Contains("neexistuje", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static (int? Id, string Number) Produced(XElement scope)
    {
        var produced = scope.Descendants().FirstOrDefault(element => element.Name.LocalName == "producedDetails");
        var idText = produced?.Elements().FirstOrDefault(element => element.Name.LocalName == "id")?.Value.Trim() ?? "";
        var number = produced?.Elements().FirstOrDefault(element => element.Name.LocalName == "number")?.Value.Trim() ?? "";
        int? id = int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        return (id, number);
    }

    private static XElement DeleteOrder(int id) =>
        new(Ord + "order",
            new XAttribute("version", "2.0"),
            new XElement(Ord + "actionType",
                new XElement(Ord + "delete",
                    new XElement(Ftr + "filter",
                        new XAttribute("agenda", ReceivedOrderAgenda),
                        new XElement(Ftr + "id", id.ToString(CultureInfo.InvariantCulture))))));

    private static XElement DocumentOrder(OrderSyncPlan plan, DispatchOrder current, DispatchOrder? lastSent, bool explicitAdd)
    {
        var lines = Lines(plan.Action, current, lastSent);
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
        if (explicitAdd)
            order.Add(new XElement(Ord + "actionType", new XElement(Ord + "add")));
        order.Add(header, detail, summary);
        return order;
    }

    private static XElement PackItem(string id, XElement order) =>
        new(Dat + "dataPackItem",
            new XAttribute("version", "2.0"),
            new XAttribute("id", id),
            order);

    private static string ItemId(string code, string suffix) =>
        Trim(string.IsNullOrEmpty(suffix) ? code : code + suffix, PackIdMax);

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

    private static string DocumentText(OrderSyncPlan plan, DispatchOrder current, DispatchOrder? lastSent)
    {
        if (plan.Action is OrderSyncAction.Correction or OrderSyncAction.Cancel)
        {
            var number = string.IsNullOrWhiteSpace(current.PohodaOrderNumber) ? current.Code : current.PohodaOrderNumber;
            var from = lastSent?.CurrentVersion ?? plan.VersionFrom;
            var label = plan.Action == OrderSyncAction.Cancel ? "Storno" : "Oprava";
            return $"{label} k {number}: {current.Code} verze {from}–{current.CurrentVersion}";
        }

        return $"{current.Code}: {current.LoadingPlace} → {current.UnloadingPlace}, {current.FuelType}";
    }

    private static string ExternalNumber(OrderSyncPlan plan, DispatchOrder current) =>
        plan.Action is OrderSyncAction.Correction or OrderSyncAction.Cancel
            ? CorrectionNumber(current.Code, plan.CorrectionIndex)
            : Trim(current.Code, NumberOrderMax);

    private static XElement Partner(DispatchOrder order)
    {
        var identity = new XElement(Ord + "partnerIdentity");
        if (order.CustomerPohodaId is int id && id >= 1)
            identity.Add(new XElement(Typ + "id", id.ToString(CultureInfo.InvariantCulture)));
        identity.Add(Address(order));
        return identity;
    }

    private static XElement Address(DispatchOrder order)
    {
        var address = new XElement(Typ + "address");
        AddAddress(address, "company", order.CustomerName, CompanyMax);
        AddAddress(address, "city", order.CustomerCity, 45);
        AddAddress(address, "street", order.CustomerStreet, 64);
        AddAddress(address, "zip", order.CustomerZip, 15);
        AddAddress(address, "ico", order.CustomerIco, IcoMax);
        AddAddress(address, "dic", order.CustomerDic, 18);
        AddAddress(address, "phone", order.CustomerPhone, 40);
        AddAddress(address, "email", order.CustomerEmail, 98);
        if (!address.HasElements)
            address.Add(new XElement(Typ + "company", Trim(string.IsNullOrWhiteSpace(order.CustomerName) ? "Zákazník" : order.CustomerName, CompanyMax)));
        return address;
    }

    private static void AddAddress(XElement address, string name, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        address.Add(new XElement(Typ + name, Trim(value.Trim(), max)));
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
