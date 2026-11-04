using PHMTRANS.Bi;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class InvoiceCustomerFilterTests
{
    [Fact]
    public void Several_customer_names_keep_only_those_invoices()
    {
        var invoices = new IssuedInvoice[]
        {
            new("1", null, null, "Alfa", 10, 0, "Neuhrazeno"),
            new("2", null, null, "Beta", 20, 0, "Neuhrazeno"),
            new("3", null, null, "alfa", 30, 0, "Neuhrazeno"),
            new("4", null, null, "Gama", 40, 0, "Neuhrazeno")
        };

        var selected = InvoiceCustomerFilter.Apply(invoices, ["Alfa", "Gama"]);
        Assert.Equal(["1", "3", "4"], selected.Select(invoice => invoice.Number));

        Assert.Equal(4, InvoiceCustomerFilter.Apply(invoices, (IReadOnlyCollection<string>?)null).Count);
        Assert.Equal(4, InvoiceCustomerFilter.Apply(invoices, []).Count);
    }
}
