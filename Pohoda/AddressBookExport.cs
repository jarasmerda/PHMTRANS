using System.Xml.Linq;

namespace PHMTRANS.Pohoda;

public static class AddressBookExport
{
    public static string CreateRequest(PohodaOptions options, string company)
    {
        XNamespace dat = "http://www.stormware.cz/schema/version_2/data.xsd";
        XNamespace lAdb = "http://www.stormware.cz/schema/version_2/list_addBook.xsd";
        XNamespace ftr = "http://www.stormware.cz/schema/version_2/filter.xsd";
        XNamespace typ = "http://www.stormware.cz/schema/version_2/type.xsd";

        var document = new XElement(dat + "dataPack",
            new XAttribute("version", "2.0"),
            new XAttribute("id", "phmtrans-adr"),
            new XAttribute("ico", options.Ico),
            new XAttribute("application", options.Application),
            new XAttribute("note", "Adresář"),
            new XAttribute(XNamespace.Xmlns + "dat", dat),
            new XAttribute(XNamespace.Xmlns + "lAdb", lAdb),
            new XAttribute(XNamespace.Xmlns + "ftr", ftr),
            new XAttribute(XNamespace.Xmlns + "typ", typ),
            new XElement(dat + "dataPackItem",
                new XAttribute("version", "2.0"),
                new XAttribute("id", "001"),
                new XElement(lAdb + "listAddressBookRequest",
                    new XAttribute("version", "2.0"),
                    new XAttribute("addressBookVersion", "2.0"),
                    new XElement(lAdb + "requestAddressBook",
                        new XElement(ftr + "filter",
                            new XElement(ftr + "companyName", company))))));

        return $"<?xml version=\"1.0\" encoding=\"Windows-1250\"?>{document.ToString(SaveOptions.DisableFormatting)}";
    }

    public static IReadOnlyList<PohodaCustomer> Read(string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new PohodaException("mServer nevrátil platné XML adresáře.", exception);
        }

        var state = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "responsePack")
            ?.Attribute("state")?.Value;
        if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase))
        {
            var note = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "note")?.Value?.Trim();
            throw new PohodaException(string.IsNullOrWhiteSpace(note) ? "Pohoda vrátila chybu při čtení adresáře." : note);
        }

        return document.Descendants()
            .Where(element => element.Name.LocalName is "addressbookHeader" or "addressbook")
            .Select(ReadCustomer)
            .Where(customer => customer is not null)
            .Select(customer => customer!)
            .GroupBy(customer => customer.Id)
            .Select(group => group.First())
            .Take(25)
            .ToArray();
    }

    private static PohodaCustomer? ReadCustomer(XElement element)
    {
        var header = element.Name.LocalName == "addressbookHeader"
            ? element
            : element.Descendants().FirstOrDefault(child => child.Name.LocalName == "addressbookHeader") ?? element;
        var idText = header.Elements().FirstOrDefault(child => child.Name.LocalName == "id")?.Value.Trim()
            ?? header.Descendants().FirstOrDefault(child => child.Name.LocalName == "id")?.Value.Trim()
            ?? "";
        if (!int.TryParse(idText, out var id) || id <= 0)
            return null;

        var company = Text(header, "company");
        var name = company.Length > 0 ? company : Text(header, "name");
        if (name.Length == 0)
            return null;
        return new PohodaCustomer(id, name, Text(header, "city"));
    }

    private static string Text(XElement root, string localName) =>
        root.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim() ?? "";
}

public sealed record PohodaCustomer(int Id, string Name, string City);
