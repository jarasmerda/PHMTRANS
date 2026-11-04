using System.Globalization;

namespace PHMTRANS.Dispatch;

public static class DispatchEndpoints
{
    private static readonly StringComparer Czech = CreateCzechComparer();

    public static void MapDispatch(this WebApplication app)
    {
        // Stejně jako /api/bi. RequireAuthorization() by skončilo prázdným 401
        // ještě před tímto middleware, protože UseAuthorization je registrované dřív.
        var api = app.MapGroup("/api/dispatch");
        api.MapGet("/board", GetBoard);
        api.MapGet("/vehicles", GetVehicles);
        api.MapGet("/drivers", GetDrivers);
        api.MapGet("/trips", GetTrips);
        api.MapPost("/trips", CreateTrip);
        api.MapPut("/trips/{id}", UpdateTrip);
        api.MapPatch("/trips/{id}", UpdateTrip);
        api.MapPost("/trips/{id}/assign", AssignTrip);
        api.MapPost("/trips/{id}/status", ChangeStatus);
        api.MapPost("/trips/{id}/cancel", CancelTrip);
        api.MapDelete("/trips/{id}", DeleteTrip);
        api.MapPost("/trips/{id}/orders", AddOrderToTrip);
        api.MapDelete("/trips/{id}/orders/{orderId}", RemoveOrderFromTrip);
        api.MapGet("/rates", GetRates);
        api.MapPut("/rates", SaveRates);
    }

    private static Task<IResult> GetBoard(string[]? status, IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var selected = StatusSelection.Parse(status, DispatchStatuses.All, DispatchStatuses.UnknownMessage);
            if (selected.Error is not null)
                return Results.BadRequest(new { message = selected.Error });

            var board = ToBoard(await store.ReadAsync(cancellationToken));
            if (selected.Values.Count == 0)
                return Results.Ok(board);

            var trips = board.Trips.Where(trip => selected.Values.Contains(trip.Status)).ToArray();
            return Results.Ok(board with { Trips = trips });
        });

    private static Task<IResult> GetVehicles(IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(ToBoard(await store.ReadAsync(cancellationToken)).Vehicles));

    private static Task<IResult> GetDrivers(IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(ToBoard(await store.ReadAsync(cancellationToken)).Drivers));

    private static Task<IResult> GetTrips(string[]? status, IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var selected = StatusSelection.Parse(status, DispatchStatuses.All, DispatchStatuses.UnknownMessage);
            if (selected.Error is not null)
                return Results.BadRequest(new { message = selected.Error });

            var trips = ToBoard(await store.ReadAsync(cancellationToken)).Trips;
            if (selected.Values.Count > 0)
                trips = trips.Where(trip => selected.Values.Contains(trip.Status)).ToArray();

            return Results.Ok(trips);
        });

    private static Task<IResult> CreateTrip(
        NewTripRequest request,
        IDispatchStore store,
        IOrderStore orders,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var mutation = await store.AddTripAsync(request, cancellationToken);
            if (mutation is not DispatchMutation.Success success)
                return ToResult(mutation);
            if (request.OrderIds is not { Count: > 0 })
                return Results.Ok(success.Trip);

            var error = await orders.AttachOrdersAsync(success.Trip.Id, request.OrderIds, onlyIfFree: true, cancellationToken);
            return error is null
                ? Results.Ok(success.Trip)
                : Results.BadRequest(new { message = error });
        });

    private static Task<IResult> UpdateTrip(
        string id,
        UpdateTripRequest request,
        IDispatchStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.UpdateTripAsync(id, request, cancellationToken)));

    private static Task<IResult> AssignTrip(
        string id,
        AssignTripRequest request,
        IDispatchStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.AssignAsync(id, request.VehicleId, request.DriverId, cancellationToken)));

    private static Task<IResult> ChangeStatus(
        string id,
        ChangeStatusRequest request,
        IDispatchStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.SetStatusAsync(id, request.Status, cancellationToken)));

    private static async Task<IResult> CancelTrip(
        string id,
        IDispatchStore store,
        IOrderStore orders,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await store.ReadAsync(cancellationToken);
            var trip = snapshot.Trips.FirstOrDefault(item => item.Id == id);
            if (trip is null)
                return Results.NotFound(new { message = "Jízda neexistuje." });
            if (trip.Status == DispatchStatuses.Stornovano)
                return Results.BadRequest(new { message = "Jízda už je stornovaná." });

            var assigned = (await orders.ListAsync(cancellationToken))
                .Where(order => string.Equals(order.TripId, id, StringComparison.Ordinal))
                .ToArray();
            foreach (var order in assigned)
            {
                var detached = await orders.AttachTripAsync(order.Id, null, onlyIfFree: false, cancellationToken);
                if (detached is not OrderMutation.Success)
                    return OrderFailure(detached);
            }

            return ToResult(await store.CancelTripAsync(id, cancellationToken));
        }
        catch (DispatchStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (OrderStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeleteTrip(
        string id,
        IDispatchStore store,
        IOrderStore orders,
        CancellationToken cancellationToken)
    {
        try
        {
            var assigned = (await orders.ListAsync(cancellationToken))
                .Count(order => string.Equals(order.TripId, id, StringComparison.Ordinal));
            return ToResult(await store.DeleteTripAsync(id, assigned, cancellationToken));
        }
        catch (DispatchStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (OrderStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult OrderFailure(OrderMutation mutation) => mutation switch
    {
        OrderMutation.NotFound => Results.NotFound(new { message = "Zakázka neexistuje." }),
        OrderMutation.Rejected rejected => Results.BadRequest(new { message = rejected.Message }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };

    private static async Task<IResult> AddOrderToTrip(
        string id,
        TripOrderRequest request,
        IOrderStore orders,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OrderId))
            return Results.BadRequest(new { message = "Vyberte zakázku." });
        return await OrderResult(() => orders.AttachTripAsync(request.OrderId, id, onlyIfFree: true, cancellationToken));
    }

    private static async Task<IResult> RemoveOrderFromTrip(
        string id,
        string orderId,
        IOrderStore orders,
        CancellationToken cancellationToken)
    {
        try
        {
            var bundle = await orders.GetAsync(orderId, cancellationToken);
            if (bundle is null)
                return Results.NotFound(new { message = "Zakázka neexistuje." });
            if (!string.Equals(bundle.Order.TripId, id, StringComparison.Ordinal))
                return Results.BadRequest(new { message = "Zakázka na této jízdě není." });
        }
        catch (OrderStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }

        return await OrderResult(() => orders.AttachTripAsync(orderId, null, onlyIfFree: false, cancellationToken));
    }

    private static Task<IResult> GetRates(IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(await store.GetRatesAsync(cancellationToken)));

    private static Task<IResult> SaveRates(
        SaveRatesRequest request,
        IDispatchStore store,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (request.DefaultCostPerKm is null)
                return Results.BadRequest(new { message = "Vyplňte výchozí nákladovou sazbu." });
            var error = await store.SaveRatesAsync(request.DefaultCostPerKm.Value, request.Vehicles ?? [], cancellationToken);
            return error is null
                ? Results.Ok(await store.GetRatesAsync(cancellationToken))
                : Results.BadRequest(new { message = error });
        });

    private static async Task<IResult> OrderResult(Func<Task<OrderMutation>> action)
    {
        try
        {
            var mutation = await action();
            return mutation switch
            {
                OrderMutation.Success success => Results.Ok(success.Order),
                OrderMutation.NotFound => Results.NotFound(new { message = "Zakázka neexistuje." }),
                OrderMutation.Rejected rejected => Results.BadRequest(new { message = rejected.Message }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        }
        catch (OrderStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DispatchStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult ToResult(DispatchMutation mutation) => mutation switch
    {
        DispatchMutation.Success success => Results.Ok(success.Trip),
        DispatchMutation.Deleted => Results.Ok(new { deleted = true }),
        DispatchMutation.NotFound => Results.NotFound(new { message = "Jízda neexistuje." }),
        DispatchMutation.Rejected rejected => Results.BadRequest(new { message = rejected.Message }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };

    private static DispatchBoard ToBoard(DispatchSnapshot snapshot)
    {
        var trips = snapshot.Trips
            .OrderBy(trip => DispatchStatuses.Rank(trip.Status))
            .ThenBy(trip => trip.PlannedStart)
            .ToArray();
        var counts = DispatchStatuses.All
            .Select(status => new DispatchStatusCount(status, trips.Count(trip => trip.Status == status)))
            .ToArray();
        var vehicles = snapshot.Vehicles.OrderBy(vehicle => vehicle.Plate, Czech).ToArray();
        var drivers = snapshot.Drivers.OrderBy(driver => driver.Name, Czech).ToArray();
        return new DispatchBoard(DispatchStorage.Kind, DispatchStorage.Note, counts, trips, vehicles, drivers);
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
