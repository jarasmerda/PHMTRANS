using System.Globalization;
using System.Xml.Linq;

namespace PHMTRANS.Pohoda;

public static class IssuedInvoiceExport
{
    public static string CreateRequest(PohodaOptions options, DateOnly from, DateOnly to)
    {
        XNamespace dat = "http://www.stormware.cz/schema/version_2/data.xsd";
        XNamespace lst = "http://www.stormware.cz/schema/version_2/list.xsd";
        XNamespace inv = "http://www.stormware.cz/schema/version_2/invoice.xsd";
        XNamespace ftr = "http://www.stormware.cz/schema/version_2/filter.xsd";
        XNamespace typ = "http://www.stormware.cz/schema/version_2/type.xsd";

        var document = new XElement(dat + "dataPack",
            new XAttribute("version", "2.0"),
            new XAttribute("id", "phmtrans-fa"),
            new XAttribute("ico", options.Ico),
            new XAttribute("application", options.Application),
            new XAttribute("note", "Vydané faktury"),
            new XAttribute(XNamespace.Xmlns + "dat", dat),
            new XAttribute(XNamespace.Xmlns + "lst", lst),
            new XAttribute(XNamespace.Xmlns + "inv", inv),
            new XAttribute(XNamespace.Xmlns + "ftr", ftr),
            new XAttribute(XNamespace.Xmlns + "typ", typ),
            new XElement(dat + "dataPackItem",
                new XAttribute("version", "2.0"),
                new XAttribute("id", "001"),
                new XElement(lst + "listInvoiceRequest",
                    new XAttribute("version", "2.0"),
                    new XAttribute("invoiceType", "issuedInvoice"),
                    new XAttribute("invoiceVersion", "2.0"),
                    new XElement(lst + "requestInvoice",
                        new XElement(ftr + "filter",
                            new XElement(ftr + "dateFrom", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                            new XElement(ftr + "dateTill", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))))));

        return $"<?xml version=\"1.0\" encoding=\"Windows-1250\"?>{document.ToString(SaveOptions.DisableFormatting)}";
    }

    public static IReadOnlyList<IssuedInvoice> Read(string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new PohodaException("mServer nevrátil platné XML faktur.", exception);
        }

        var state = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "responsePack")
            ?.Attribute("state")?.Value;
        if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase))
        {
            var note = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "note")?.Value?.Trim();
            throw new PohodaException(string.IsNullOrWhiteSpace(note) ? "Pohoda vrátila chybu při čtení faktur." : note);
        }

        return document.Descendants()
            .Where(element => element.Name.LocalName == "invoice" && element.Elements().Any(child => child.Name.LocalName == "invoiceHeader"))
            .Select(ReadInvoice)
            .OrderByDescending(invoice => invoice.Date)
            .ThenByDescending(invoice => invoice.Number, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IssuedInvoice ReadInvoice(XElement invoice)
    {
        var header = invoice.Elements().First(element => element.Name.LocalName == "invoiceHeader");
        var number = Text(header, "numberRequested");
        if (number.Length == 0)
            number = Text(header, "ids");

        var customer = Text(header, "company");
        if (customer.Length == 0)
            customer = Text(header, "name");

        var total = HomeTotal(invoice);
        var paid = PaidAmount(invoice);
        var date = Date(header, "date");
        var due = Date(header, "dateDue");
        return new IssuedInvoice(number, date, due, customer, total, paid, Status(total, paid, due));
    }

    private static decimal HomeTotal(XElement invoice)
    {
        var summary = invoice.Descendants().FirstOrDefault(element => element.Name.LocalName == "invoiceSummary");
        var home = summary?.Descendants().FirstOrDefault(element => element.Name.LocalName == "homeCurrency");
        if (home is null)
            return 0;

        var total = Amount(home, "priceNone") + Amount(home, "priceLowSum") + Amount(home, "priceHighSum") + Amount(home, "price3Sum");
        if (total != 0)
            return total;

        return Amount(home, "priceNone")
            + Amount(home, "priceLow") + Amount(home, "priceLowVAT")
            + Amount(home, "priceHigh") + Amount(home, "priceHighVAT")
            + Amount(home, "price3") + Amount(home, "price3VAT");
    }

    private static decimal PaidAmount(XElement invoice)
    {
        var items = invoice.Descendants().Where(element => element.Name.LocalName == "liquidationItem").ToArray();
        if (items.Length > 0)
            return items.Sum(item => Amount(item, "amountHome"));

        return invoice.Descendants()
            .Where(element => element.Name.LocalName == "liquidation")
            .Sum(element => Amount(element, "amountHome"));
    }

    private static string Status(decimal total, decimal paid, DateOnly? due)
    {
        var remaining = total - paid;
        if (remaining <= 0.5m)
            return "Uhrazeno";
        if (paid > 0.5m)
            return "Částečně";
        if (due is not null && due.Value < DateOnly.FromDateTime(DateTime.Today))
            return "Po splatnosti";
        return "Neuhrazeno";
    }

    private static string Text(XElement root, string localName) =>
        root.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim() ?? "";

    private static DateOnly? Date(XElement root, string localName)
    {
        var value = root.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim();
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static decimal Amount(XElement root, string localName)
    {
        var value = root.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim();
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : 0;
    }
}

public sealed record IssuedInvoice(
    string Number,
    DateOnly? Date,
    DateOnly? DueDate,
    string Customer,
    decimal Total,
    decimal Paid,
    string Status);
