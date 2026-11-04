using System.Globalization;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Bi;

public static class InvoiceCustomerFilter
{
    private static readonly StringComparer Czech = CreateCzechComparer();

    public static IReadOnlyList<string> Names(IReadOnlyList<IssuedInvoice> invoices) =>
        invoices
            .Select(invoice => invoice.Customer.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, Czech)
            .ToArray();

    public static IReadOnlyList<IssuedInvoice> Apply(IReadOnlyList<IssuedInvoice> invoices, string? customer) =>
        Apply(invoices, string.IsNullOrWhiteSpace(customer) ? null : [customer.Trim()]);

    public static IReadOnlyList<IssuedInvoice> Apply(IReadOnlyList<IssuedInvoice> invoices, IReadOnlyCollection<string>? customers)
    {
        if (customers is null || customers.Count == 0)
            return invoices;

        var names = new HashSet<string>(
            customers.Select(name => name.Trim()).Where(name => name.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
            return invoices;

        return invoices
            .Where(invoice => names.Contains(invoice.Customer.Trim()))
            .ToArray();
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
}

public sealed class IssuedInvoiceCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateOnly From, DateOnly To, IReadOnlyList<IssuedInvoice> Invoices)? _entry;

    public async Task<IReadOnlyList<IssuedInvoice>> GetOrLoadAsync(
        DateOnly from,
        DateOnly to,
        bool refresh,
        Func<CancellationToken, Task<IReadOnlyList<IssuedInvoice>>> load,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _entry is { } cached && cached.From == from && cached.To == to)
                return cached.Invoices;

            var invoices = await load(cancellationToken);
            _entry = (from, to, invoices);
            return invoices;
        }
        finally
        {
            _gate.Release();
        }
    }
}
