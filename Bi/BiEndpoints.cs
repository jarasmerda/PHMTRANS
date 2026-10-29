using PHMTRANS.Pohoda;

namespace PHMTRANS.Bi;

public static class BiEndpoints
{
    public static void MapBi(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim()
                ?? context.Request.Host.Host;
            var path = context.Request.Path.Value ?? "";
            if (IsErpHost(host) && path is "" or "/" or "/index.html")
                context.Request.Path = "/erp.html";

            await next();
        });

        app.MapGet("/api/bi/issued-invoices", async (
            DateOnly? from,
            DateOnly? to,
            PohodaMServerClient pohoda,
            CancellationToken cancellationToken) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var start = from ?? new DateOnly(today.Year, 1, 1);
            var end = to ?? today;
            if (end < start)
                return Results.BadRequest(new { message = "Datum do je před datem od." });

            try
            {
                var invoices = await pohoda.ListIssuedInvoicesAsync(start, end, cancellationToken);
                return Results.Ok(CreateReport(start, end, invoices));
            }
            catch (PohodaException exception)
            {
                return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    private static bool IsErpHost(string host)
    {
        var name = host.Split(':')[0];
        return name.Equals("www.erp.phmtrans.cz", StringComparison.OrdinalIgnoreCase)
            || name.Equals("erp.phmtrans.cz", StringComparison.OrdinalIgnoreCase);
    }

    private static IssuedInvoiceReport CreateReport(DateOnly from, DateOnly to, IReadOnlyList<IssuedInvoice> invoices)
    {
        var total = invoices.Sum(invoice => invoice.Total);
        var paid = invoices.Sum(invoice => invoice.Paid);
        var unpaid = invoices.Sum(invoice => Math.Max(0, invoice.Total - invoice.Paid));
        var overdue = invoices
            .Where(invoice => invoice.Status == "Po splatnosti")
            .Sum(invoice => Math.Max(0, invoice.Total - invoice.Paid));

        var months = new List<InvoiceMonth>();
        for (var cursor = new DateOnly(from.Year, from.Month, 1); cursor <= to; cursor = cursor.AddMonths(1))
        {
            var inMonth = invoices.Where(invoice => invoice.Date is { } date && date.Year == cursor.Year && date.Month == cursor.Month).ToArray();
            months.Add(new InvoiceMonth($"{cursor.Year:0000}-{cursor.Month:00}", inMonth.Sum(invoice => invoice.Total), inMonth.Length));
        }

        return new IssuedInvoiceReport(from, to, invoices.Count, total, paid, unpaid, overdue, months, invoices);
    }
}

public sealed record InvoiceMonth(string Month, decimal Total, int Count);

public sealed record IssuedInvoiceReport(
    DateOnly From,
    DateOnly To,
    int Count,
    decimal Total,
    decimal Paid,
    decimal Unpaid,
    decimal Overdue,
    IReadOnlyList<InvoiceMonth> Months,
    IReadOnlyList<IssuedInvoice> Invoices);
