using System.Globalization;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Bi;

// Měsíční přehled vydaných faktur bez DPH po odběratelích.
// Odběratelé ze skupiny B adresáře Pohody (dbo.AD.Skupina) jsou ve druhé sekci, ostatní v první.
public static class MonthlyInvoiceReportBuilder
{
    public const string SeparateGroup = "B";

    private static readonly StringComparer Czech = CreateCzechComparer();

    public static MonthlyInvoiceReport Create(
        int year,
        IReadOnlyList<IssuedInvoice> invoices,
        IReadOnlyList<PohodaAddressGroup> groups)
    {
        var byId = new Dictionary<int, string?>();
        var byIco = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            byId.TryAdd(group.Id, group.Group);
            if (!string.IsNullOrWhiteSpace(group.Ico))
                byIco.TryAdd(group.Ico.Trim(), group.Group);
        }

        var main = new Dictionary<string, MonthlyRowBuilder>(StringComparer.OrdinalIgnoreCase);
        var separate = new Dictionary<string, MonthlyRowBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var invoice in invoices)
        {
            if (invoice.Date is not { } date || date.Year != year)
                continue;

            var group = GroupOf(invoice, byId, byIco);
            var target = string.Equals(group?.Trim(), SeparateGroup, StringComparison.OrdinalIgnoreCase) ? separate : main;
            var key = Key(invoice);
            if (!target.TryGetValue(key, out var row))
            {
                row = new MonthlyRowBuilder(invoice.Customer.Trim());
                target[key] = row;
            }

            if (row.Customer.Length == 0)
                row.Customer = invoice.Customer.Trim();
            row.Months[date.Month - 1] += invoice.TotalNoVat;
        }

        return new MonthlyInvoiceReport(year, Section(main), Section(separate));
    }

    public static decimal[] Quarters(IReadOnlyList<decimal> months) =>
        Enumerable.Range(0, 4).Select(quarter => months.Skip(quarter * 3).Take(3).Sum()).ToArray();

    private static string? GroupOf(
        IssuedInvoice invoice,
        IReadOnlyDictionary<int, string?> byId,
        IReadOnlyDictionary<string, string?> byIco)
    {
        if (invoice.PartnerId is { } id && byId.TryGetValue(id, out var group))
            return group;
        if (!string.IsNullOrWhiteSpace(invoice.Ico) && byIco.TryGetValue(invoice.Ico.Trim(), out group))
            return group;
        return null;
    }

    private static string Key(IssuedInvoice invoice)
    {
        if (invoice.PartnerId is { } id)
            return "id:" + id.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(invoice.Ico))
            return "ico:" + invoice.Ico.Trim();
        return "name:" + invoice.Customer.Trim();
    }

    private static MonthlySection Section(Dictionary<string, MonthlyRowBuilder> rows)
    {
        var list = rows.Values
            .Select(row => row.Build())
            .OrderByDescending(row => row.Total)
            .ThenBy(row => row.Customer, Czech)
            .ToArray();
        var months = Enumerable.Range(0, 12).Select(month => list.Sum(row => row.Months[month])).ToArray();
        return new MonthlySection(list, months, Quarters(months), months.Sum());
    }

    private static StringComparer CreateCzechComparer()
    {
        try
        {
            return StringComparer.Create(CultureInfo.GetCultureInfo("cs-CZ"), ignoreCase: true);
        }
        catch (CultureNotFoundException)
        {
            return StringComparer.OrdinalIgnoreCase;
        }
    }

    private sealed class MonthlyRowBuilder(string customer)
    {
        public string Customer { get; set; } = customer;

        public decimal[] Months { get; } = new decimal[12];

        public MonthlyRow Build() => new(Customer.Length > 0 ? Customer : "(bez názvu)", Months, Quarters(Months), Months.Sum());
    }
}

public sealed record MonthlyRow(string Customer, IReadOnlyList<decimal> Months, IReadOnlyList<decimal> Quarters, decimal Total);

public sealed record MonthlySection(
    IReadOnlyList<MonthlyRow> Rows,
    IReadOnlyList<decimal> Months,
    IReadOnlyList<decimal> Quarters,
    decimal Total);

public sealed record MonthlyInvoiceReport(int Year, MonthlySection Main, MonthlySection GroupB);
