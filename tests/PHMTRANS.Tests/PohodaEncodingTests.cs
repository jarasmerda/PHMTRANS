using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHMTRANS.Pohoda;
using Xunit;

namespace PHMTRANS.Tests;

// Česká diakritika v názvech odběratelů musí projít celou cestou z mServeru až do faktury.
public sealed class PohodaEncodingTests
{
    private const string Names = "Onyx Real Bučovice s.r.o.|Miroslav Matušů|Recyklace Hrbáček, s.r.o.|Společenství vlastníků jednotek Československé armády";

    static PohodaEncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Theory]
    [InlineData("Windows-1250", 1250, "text/xml")]
    [InlineData("Windows-1250", 1250, "text/xml; charset=windows-1250")]
    [InlineData("UTF-8", 65001, "text/xml; charset=utf-8")]
    [InlineData("UTF-8", 65001, "text/xml; charset=windows-1250")]
    [InlineData("utf-8", 65001, "text/xml")]
    public async Task Customer_names_keep_czech_diacritics(string declared, int codePage, string contentType)
    {
        var expected = Names.Split('|');
        var xml = Response(declared, expected);
        var bytes = Encoding.GetEncoding(codePage).GetBytes(xml);
        var client = Client(bytes, contentType);

        var invoices = await client.ListIssuedInvoicesAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), CancellationToken.None);

        Assert.Equal(expected.OrderBy(name => name), invoices.Select(invoice => invoice.Customer).OrderBy(name => name));
    }

    private static PohodaMServerClient Client(byte[] body, string contentType)
    {
        var handler = new FixedHandler(body, contentType);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://pohoda.test/") };
        var options = Options.Create(new PohodaOptions { BaseUrl = "http://pohoda.test", Username = "u", Password = "p", Ico = "12345678" });
        return new PohodaMServerClient(http, options, NullLogger<PohodaMServerClient>.Instance);
    }

    private static string Response(string declared, IEnumerable<string> customers)
    {
        var invoices = string.Concat(customers.Select((name, index) => $"""
            <lst:invoice version="2.0">
              <inv:invoiceHeader>
                <inv:number><typ:numberRequested>FV{index}</typ:numberRequested></inv:number>
                <inv:date>2026-01-1{index}</inv:date>
                <inv:partnerIdentity><typ:id>{index + 1}</typ:id><typ:address><typ:company>{name}</typ:company></typ:address></inv:partnerIdentity>
              </inv:invoiceHeader>
              <inv:invoiceSummary><inv:homeCurrency><typ:priceNone>100</typ:priceNone></inv:homeCurrency></inv:invoiceSummary>
            </lst:invoice>
            """));
        return $"""
            <?xml version="1.0" encoding="{declared}"?>
            <rsp:responsePack version="2.0" state="ok" xmlns:rsp="http://www.stormware.cz/schema/version_2/response.xsd" xmlns:lst="http://www.stormware.cz/schema/version_2/list.xsd" xmlns:inv="http://www.stormware.cz/schema/version_2/invoice.xsd" xmlns:typ="http://www.stormware.cz/schema/version_2/type.xsd">
              <rsp:responsePackItem state="ok"><lst:listInvoice state="ok">{invoices}</lst:listInvoice></rsp:responsePackItem>
            </rsp:responsePack>
            """;
    }

    private sealed class FixedHandler(byte[] body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
