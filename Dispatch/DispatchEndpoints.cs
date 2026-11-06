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
        api.MapGet("/calendar", GetCalendar);
        api.MapGet("/rates", GetRates);
        api.MapPut("/rates", SaveRates);
    }

    private static Task<IResult> GetBoard(IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(ToBoard(await store.ReadAsync(cancellationToken))));

    private static Task<IResult> GetVehicles(IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(ToBoard(await store.ReadAsync(cancellationToken)).Vehicles));

    private static Task<IResult> GetDrivers(IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(ToBoard(await store.ReadAsync(cancellationToken)).Drivers));

    private static async Task<IResult> GetCalendar(
        string? from,
        int? days,
        IDispatchStore store,
        IOrderStore orders,
        CancellationToken cancellationToken)
    {
        var range = DriverCalendar.ParseRange(from, days, DateOnly.FromDateTime(DateTime.Today));
        if (range.Error is not null)
            return Results.BadRequest(new { message = range.Error });
        try
        {
            var snapshot = await store.ReadAsync(cancellationToken);
            var list = await orders.ListAsync(cancellationToken);
            var drivers = snapshot.Drivers.OrderBy(driver => driver.Name, Czech).ToArray();
            return Results.Ok(DriverCalendar.Build(drivers, snapshot.Vehicles, list, range.From, range.Days));
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

    private static DispatchBoard ToBoard(DispatchSnapshot snapshot)
    {
        var vehicles = snapshot.Vehicles.OrderBy(vehicle => vehicle.Plate, Czech).ToArray();
        var drivers = snapshot.Drivers.OrderBy(driver => driver.Name, Czech).ToArray();
        return new DispatchBoard(DispatchStorage.Kind, DispatchStorage.Note, vehicles, drivers);
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
