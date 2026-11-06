using PHMTRANS.Bi;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class MonthlyInvoiceReportTests
{
    [Fact]
    public void Group_b_customers_go_to_the_second_section_and_months_sum_into_quarters()
    {
        var invoices = new IssuedInvoice[]
        {
            Invoice("1", new DateOnly(2026, 1, 15), "ČSAD Hodonín a.s.", 1000, partnerId: 10),
            Invoice("2", new DateOnly(2026, 3, 2), "ČSAD Hodonín a.s.", 500, partnerId: 10),
            Invoice("3", new DateOnly(2026, 4, 1), "ČSAD Hodonín", 200, partnerId: 10),
            Invoice("4", new DateOnly(2026, 2, 10), "JRA Estate, a.s.", 28900, partnerId: 20),
            Invoice("5", new DateOnly(2026, 11, 5), "Obec Zbýšov", 300, ico: "00282821"),
            Invoice("6", new DateOnly(2025, 12, 31), "Loňská s.r.o.", 999, partnerId: 30),
            Invoice("7", new DateOnly(2026, 5, 5), "Bez adresáře s.r.o.", 50)
        };
        var groups = new[]
        {
            new PohodaAddressGroup(10, "11111111", "A"),
            new PohodaAddressGroup(20, "22222222", " b "),
            new PohodaAddressGroup(40, "00282821", "B")
        };

        var report = MonthlyInvoiceReportBuilder.Create(2026, invoices, groups);

        Assert.Equal(2026, report.Year);
        Assert.Equal(["ČSAD Hodonín a.s.", "Bez adresáře s.r.o."], report.Main.Rows.Select(row => row.Customer));
        var csad = report.Main.Rows[0];
        Assert.Equal(1000m, csad.Months[0]);
        Assert.Equal(500m, csad.Months[2]);
        Assert.Equal(200m, csad.Months[3]);
        Assert.Equal([1500m, 200m, 0m, 0m], csad.Quarters);
        Assert.Equal(1700m, csad.Total);
        Assert.Equal(1750m, report.Main.Total);
        Assert.Equal(12, report.Main.Months.Count);

        Assert.Equal(["JRA Estate, a.s.", "Obec Zbýšov"], report.GroupB.Rows.Select(row => row.Customer));
        Assert.Equal(28900m, report.GroupB.Months[1]);
        Assert.Equal(300m, report.GroupB.Quarters[3]);
        Assert.Equal(29200m, report.GroupB.Total);
    }

    [Fact]
    public void Address_book_names_replace_names_from_invoices()
    {
        var invoices = new IssuedInvoice[]
        {
            Invoice("1", new DateOnly(2026, 1, 1), "Onyx Real Bucovice s.r.o.", 100, partnerId: 7),
            Invoice("2", new DateOnly(2026, 1, 2), "Miroslav Matusu", 100, ico: "11223344"),
            Invoice("3", new DateOnly(2026, 1, 3), "Bez adresare", 100, partnerId: 99)
        };
        var addresses = new[]
        {
            new PohodaAddressGroup(7, "12345678", "B", "Onyx Real Bučovice s.r.o."),
            new PohodaAddressGroup(8, "11223344", null, "Miroslav Matušů"),
            new PohodaAddressGroup(99, null, null, null)
        };

        var named = AddressBookNames.Apply(invoices, addresses);

        Assert.Equal(["Onyx Real Bučovice s.r.o.", "Miroslav Matušů", "Bez adresare"], named.Select(invoice => invoice.Customer));
        Assert.Equal("Bucovice", invoices[0].Customer.Split(' ')[2]);
    }

    [Fact]
    public void Invoice_export_reads_partner_id_ico_and_amount_without_vat()
    {
        const string xml = """
            <rsp:responsePack xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd" state="ok">
              <rsp:responsePackItem state="ok">
                <lst:listInvoice xmlns:lst="http://www.stormware.cz/schema/version_2/list.xsd">
                  <lst:invoice xmlns:inv="http://www.stormware.cz/schema/version_2/invoice.xsd" xmlns:typ="http://www.stormware.cz/schema/version_2/type.xsd">
                    <inv:invoiceHeader>
                      <inv:number><typ:numberRequested>FV2026001</typ:numberRequested></inv:number>
                      <inv:date>2026-02-03</inv:date>
                      <inv:dateDue>2026-02-17</inv:dateDue>
                      <inv:partnerIdentity>
                        <typ:id>25</typ:id>
                        <typ:address><typ:company>ČSAD Hodonín a.s.</typ:company><typ:ico>12345678</typ:ico></typ:address>
                      </inv:partnerIdentity>
                    </inv:invoiceHeader>
                    <inv:invoiceSummary>
                      <inv:homeCurrency>
                        <typ:priceNone>100</typ:priceNone>
                        <typ:priceHigh>1000</typ:priceHigh>
                        <typ:priceHighVAT>210</typ:priceHighVAT>
                        <typ:priceHighSum>1210</typ:priceHighSum>
                      </inv:homeCurrency>
                    </inv:invoiceSummary>
                  </lst:invoice>
                </lst:listInvoice>
              </rsp:responsePackItem>
            </rsp:responsePack>
            """;

        var invoice = Assert.Single(IssuedInvoiceExport.Read(xml));

        Assert.Equal(25, invoice.PartnerId);
        Assert.Equal("12345678", invoice.Ico);
        Assert.Equal("ČSAD Hodonín a.s.", invoice.Customer);
        Assert.Equal(1100m, invoice.TotalNoVat);
        Assert.Equal(1310m, invoice.Total);
    }

    private static IssuedInvoice Invoice(string number, DateOnly date, string customer, decimal noVat, int? partnerId = null, string? ico = null) =>
        new(number, date, null, customer, noVat * 1.21m, 0, "Neuhrazeno", noVat, partnerId, ico);
}
