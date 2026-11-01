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
            var authenticated = context.User.Identity?.IsAuthenticated == true;
            var invoiceApi = path.StartsWith("/api/bi", StringComparison.OrdinalIgnoreCase);

            if (!authenticated && (invoiceApi || path is "/erp.html"))
            {
                if (invoiceApi)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { message = "Přihlaste se." });
                    return;
                }

                context.Response.Redirect("/login.html");
                return;
            }

            if (IsErpHost(host) && !authenticated && path is "" or "/" or "/index.html")
            {
                context.Response.Redirect("/login.html");
                return;
            }

            if (IsErpHost(host) && authenticated && path is "" or "/" or "/index.html" or "/login.html")
                context.Request.Path = "/erp.html";

            await next();
        });

        app.MapGet("/api/bi/issued-invoices", async (
            DateOnly? from,
            DateOnly? to,
            string? customer,
            bool? refresh,
            IssuedInvoiceCache cache,
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
                var invoices = await cache.GetOrLoadAsync(
                    start,
                    end,
                    refresh ?? true,
                    token => pohoda.ListIssuedInvoicesAsync(start, end, token),
                    cancellationToken);
                var selected = string.IsNullOrWhiteSpace(customer) ? null : customer.Trim();
                return Results.Ok(CreateReport(start, end, invoices, selected));
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

    private static IssuedInvoiceReport CreateReport(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<IssuedInvoice> invoices,
        string? customer)
    {
        var customers = InvoiceCustomerFilter.Names(invoices);
        var filtered = InvoiceCustomerFilter.Apply(invoices, customer);
        var total = filtered.Sum(invoice => invoice.Total);
        var paid = filtered.Sum(invoice => invoice.Paid);
        var unpaid = filtered.Sum(invoice => Math.Max(0, invoice.Total - invoice.Paid));
        var overdue = filtered
            .Where(invoice => invoice.Status == "Po splatnosti")
            .Sum(invoice => Math.Max(0, invoice.Total - invoice.Paid));

        var months = new List<InvoiceMonth>();
        for (var cursor = new DateOnly(from.Year, from.Month, 1); cursor <= to; cursor = cursor.AddMonths(1))
        {
            var inMonth = filtered.Where(invoice => invoice.Date is { } date && date.Year == cursor.Year && date.Month == cursor.Month).ToArray();
            months.Add(new InvoiceMonth($"{cursor.Year:0000}-{cursor.Month:00}", inMonth.Sum(invoice => invoice.Total), inMonth.Length));
        }

        return new IssuedInvoiceReport(from, to, filtered.Count, total, paid, unpaid, overdue, months, filtered, customers, customer);
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
    IReadOnlyList<IssuedInvoice> Invoices,
    IReadOnlyList<string> Customers,
    string? CustomerFilter);
