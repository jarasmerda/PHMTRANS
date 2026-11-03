using Microsoft.Extensions.Options;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Dispatch;

public static class OrderEndpoints
{
    public static void MapOrders(this WebApplication app)
    {
        var api = app.MapGroup("/api/dispatch");
        api.MapGet("/orders", ListOrders);
        api.MapGet("/orders/{id}", GetOrder);
        api.MapPost("/orders", CreateOrder);
        api.MapPut("/orders/{id}", UpdateOrder);
        api.MapPost("/orders/{id}/confirm", ConfirmOrder);
        api.MapPost("/orders/{id}/invoiced", MarkInvoiced);
        api.MapPost("/orders/{id}/resend", ResendOrder);
        api.MapGet("/sync", ListSync);
        api.MapGet("/customers", FindCustomers);
    }

    private static Task<IResult> ListOrders(
        string? status,
        IOrderStore store,
        IOptions<PohodaOptions> options,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (!string.IsNullOrWhiteSpace(status) && !OrderStatuses.IsKnown(status.Trim()))
                return Results.BadRequest(new { message = OrderStatuses.UnknownMessage });

            var orders = await store.ListAsync(cancellationToken);
            var counts = OrderStatuses.All
                .Select(item => new OrderStatusCount(item, orders.Count(order => order.Status == item)))
                .ToArray();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var selected = status.Trim();
                orders = orders.Where(order => order.Status == selected).ToArray();
            }

            var sorted = orders
                .OrderBy(order => OrderStatuses.Rank(order.Status))
                .ThenBy(order => order.LoadingDate)
                .ThenBy(order => order.Code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var mode = options.Value.EffectiveSyncMode();
            return Results.Ok(new OrderListResponse(mode, OrderSyncMode.Describe(mode), counts, sorted));
        });

    private static Task<IResult> GetOrder(
        string id,
        IOrderStore store,
        IOptions<PohodaOptions> options,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var bundle = await store.GetAsync(id, cancellationToken);
            if (bundle is null)
                return Results.NotFound(new { message = "Zakázka neexistuje." });

            var mode = options.Value.EffectiveSyncMode();
            return Results.Ok(new OrderDetailResponse(bundle.Order, bundle.Versions, bundle.Sync, mode, OrderSyncMode.Describe(mode)));
        });

    private static Task<IResult> CreateOrder(OrderSaveRequest request, IOrderStore store, CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.CreateAsync(request, cancellationToken)));

    private static Task<IResult> UpdateOrder(
        string id,
        OrderSaveRequest request,
        IOrderStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.UpdateAsync(id, request, cancellationToken)));

    private static Task<IResult> ConfirmOrder(string id, IOrderStore store, CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.ConfirmAsync(id, cancellationToken)));

    private static Task<IResult> MarkInvoiced(
        string id,
        MarkInvoicedRequest? request,
        IOrderStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.MarkInvoicedAsync(id, request?.InvoiceNumber, cancellationToken)));

    private static Task<IResult> ResendOrder(string id, IOrderStore store, CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.ResendAsync(id, cancellationToken)));

    private static Task<IResult> ListSync(IOrderStore store, IOptions<PohodaOptions> options, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var mode = options.Value.EffectiveSyncMode();
            var items = await store.ListQueueAsync(cancellationToken);
            return Results.Ok(new OrderSyncResponse(mode, OrderSyncMode.Describe(mode), items));
        });

    private static async Task<IResult> FindCustomers(
        string? q,
        PohodaMServerClient pohoda,
        CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        if (query.Length < 2)
            return Results.Ok(Array.Empty<PohodaCustomer>());

        try
        {
            return Results.Ok(await pohoda.FindCustomersAsync(query, cancellationToken));
        }
        catch (PohodaException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (OrderStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult ToResult(OrderMutation mutation) => mutation switch
    {
        OrderMutation.Success success => Results.Ok(success.Order),
        OrderMutation.NotFound => Results.NotFound(new { message = "Zakázka neexistuje." }),
        OrderMutation.Rejected rejected => Results.BadRequest(new { message = rejected.Message }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };
}

public sealed record MarkInvoicedRequest(string? InvoiceNumber);

public sealed record OrderSyncResponse(
    string SyncMode,
    string SyncNote,
    IReadOnlyList<OrderSyncEntry> Items);
