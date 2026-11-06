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
        api.MapGet("/fuel-types", ListFuelTypes);
        api.MapGet("/loading-places", ListLoadingPlaces);
        api.MapPost("/distances/lookup", LookupDistances);
        api.MapPost("/distances", SaveDistance);
        api.MapPost("/orders", CreateOrder);
        api.MapPut("/orders/{id}", UpdateOrder);
        api.MapPost("/orders/{id}/confirm", ConfirmOrder);
        api.MapPost("/orders/{id}/invoiced", MarkInvoiced);
        api.MapPost("/orders/{id}/resend", ResendOrder);
        api.MapPost("/orders/{id}/cancel", CancelOrder);
        api.MapGet("/sync", ListSync);
        api.MapGet("/customers", FindCustomers);
    }

    private static Task<IResult> ListOrders(
        string[]? status,
        IOrderStore store,
        IOptions<PohodaOptions> options,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var selected = StatusSelection.ParseList(status, OrderStatuses.All, OrderStatuses.UnknownMessage, OrderStatuses.Stornovano);
            if (selected.Error is not null)
                return Results.BadRequest(new { message = selected.Error });

            var orders = await store.ListAsync(cancellationToken);
            var counts = OrderStatuses.All
                .Select(item => new OrderStatusCount(item, orders.Count(order => order.Status == item)))
                .ToArray();
            orders = selected.Restrict
                ? OrderListFilter.Apply(orders, selected.Values)
                : orders.ToArray();

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

    private static Task<IResult> ListFuelTypes(IOrderStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(await store.ListFuelTypesAsync(cancellationToken)));

    private static Task<IResult> ListLoadingPlaces(IOrderStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(await store.ListLoadingPlacesAsync(cancellationToken)));

    private static Task<IResult> LookupDistances(
        DistanceLookupRequest? request,
        IOrderStore store,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var pairs = request?.Pairs ?? [];
            if (pairs.Count > 40)
                return Results.BadRequest(new { message = "Najednou lze ověřit nejvýše 40 úseků." });
            return Results.Ok(new { pairs = await store.LookupDistancesAsync(pairs, cancellationToken) });
        });

    private static Task<IResult> SaveDistance(
        DistanceSaveRequest? request,
        IOrderStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var (write, error) = DistanceSave.Validate(request);
            if (error is not null)
                return Results.BadRequest(new { message = error });
            await store.SaveDistanceAsync(write!, Actor(httpContext), cancellationToken);
            return Results.Ok(new { from = write!.FromPlace, to = write.ToPlace, km = write.Km });
        });

    private static Task<IResult> CreateOrder(
        OrderSaveRequest request,
        IOrderStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.CreateAsync(request, cancellationToken, Actor(httpContext))));

    private static Task<IResult> UpdateOrder(
        string id,
        OrderSaveRequest request,
        IOrderStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.UpdateAsync(id, request, cancellationToken, Actor(httpContext))));

    private static string? Actor(HttpContext httpContext)
    {
        var display = httpContext.User.FindFirst("display_name")?.Value;
        var name = string.IsNullOrWhiteSpace(display) ? httpContext.User.Identity?.Name : display;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

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

    private static Task<IResult> CancelOrder(
        string id,
        CancelOrderRequest? request,
        IOrderStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.CancelAsync(id, request?.Reason, request?.Delete == true, cancellationToken)));

    private static Task<IResult> ListSync(IOrderStore store, IOptions<PohodaOptions> options, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var mode = options.Value.EffectiveSyncMode();
            var items = await store.ListQueueAsync(cancellationToken);
            return Results.Ok(new OrderSyncResponse(mode, OrderSyncMode.Describe(mode), items));
        });

    private static async Task<IResult> FindCustomers(
        string? q,
        IPohodaAddressBook addressBook,
        CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        if (query.Length < 2)
            return Results.Ok(Array.Empty<PohodaAddress>());

        try
        {
            return Results.Ok(await addressBook.SearchAsync(query, cancellationToken));
        }
        catch (PohodaAddressBookException exception)
        {
            var status = exception.PermissionDenied
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status503ServiceUnavailable;
            return Results.Json(new { message = exception.Message }, statusCode: status);
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
        OrderMutation.Deleted => Results.Ok(new { deleted = true }),
        OrderMutation.NotFound => Results.NotFound(new { message = "Zakázka neexistuje." }),
        OrderMutation.Rejected rejected => Results.BadRequest(new { message = rejected.Message }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };
}

public sealed record MarkInvoicedRequest(string? InvoiceNumber);

public sealed record DistanceLookupRequest(IReadOnlyList<DistanceQuery>? Pairs);

public sealed record OrderSyncResponse(
    string SyncMode,
    string SyncNote,
    IReadOnlyList<OrderSyncEntry> Items);

public static class OrderListFilter
{
    public static IReadOnlyList<DispatchOrder> Apply(IReadOnlyList<DispatchOrder> orders, string? status) =>
        Apply(orders, string.IsNullOrWhiteSpace(status) ? null : [status.Trim()]);

    public static IReadOnlyList<DispatchOrder> Apply(IReadOnlyList<DispatchOrder> orders, IReadOnlyCollection<string>? statuses)
    {
        if (statuses is null || statuses.Count == 0)
            return orders.ToArray();

        var selected = new HashSet<string>(statuses, StringComparer.Ordinal);
        return orders.Where(order => selected.Contains(order.Status)).ToArray();
    }
}
