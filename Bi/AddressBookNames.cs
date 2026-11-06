using PHMTRANS.Pohoda;

namespace PHMTRANS.Bi;

// Název odběratele z adresáře Pohody (dbo.AD) přes SQL.
// Kopie adresy na faktuře z mServeru může mít ztracenou diakritiku (č → c, ů → u), adresář ji má správně.
public static class AddressBookNames
{
    public static (int[] Ids, string[] Icos) Keys(IReadOnlyList<IssuedInvoice> invoices)
    {
        var ids = invoices.Where(invoice => invoice.PartnerId is not null).Select(invoice => invoice.PartnerId!.Value).Distinct().ToArray();
        var icos = invoices
            .Where(invoice => invoice.PartnerId is null && !string.IsNullOrWhiteSpace(invoice.Ico))
            .Select(invoice => invoice.Ico!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (ids, icos);
    }

    public static IReadOnlyList<IssuedInvoice> Apply(IReadOnlyList<IssuedInvoice> invoices, IReadOnlyList<PohodaAddressGroup> addresses)
    {
        var byId = new Dictionary<int, string>();
        var byIco = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address.Name))
                continue;
            byId.TryAdd(address.Id, address.Name);
            if (!string.IsNullOrWhiteSpace(address.Ico))
                byIco.TryAdd(address.Ico.Trim(), address.Name);
        }

        return invoices.Select(invoice =>
        {
            if (invoice.PartnerId is { } id && byId.TryGetValue(id, out var name))
                return invoice with { Customer = name };
            if (invoice.PartnerId is null && !string.IsNullOrWhiteSpace(invoice.Ico) && byIco.TryGetValue(invoice.Ico.Trim(), out name))
                return invoice with { Customer = name };
            return invoice;
        }).ToArray();
    }

    /// <summary>Doplní názvy z adresáře. Když adresář není dostupný, vrátí faktury beze změny.</summary>
    public static async Task<IReadOnlyList<IssuedInvoice>> TryApplyAsync(
        IReadOnlyList<IssuedInvoice> invoices,
        IPohodaAddressBook addressBook,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var (ids, icos) = Keys(invoices);
        try
        {
            return Apply(invoices, await addressBook.GroupsAsync(ids, icos, cancellationToken));
        }
        catch (PohodaAddressBookException exception)
        {
            logger.LogWarning("Názvy odběratelů z adresáře Pohody se nenačetly, zůstávají názvy z faktur. {Message}", exception.Message);
            return invoices;
        }
    }
}
