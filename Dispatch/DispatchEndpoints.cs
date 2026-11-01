using System.Globalization;

namespace PHMTRANS.Dispatch;

public static class DispatchEndpoints
{
    private static readonly StringComparer Czech = CreateCzechComparer();

    public static void MapDispatch(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim()
                ?? context.Request.Host.Host;
            var path = context.Request.Path.Value ?? "";
            var authenticated = context.User.Identity?.IsAuthenticated == true;
            var dispatchApi = context.Request.Path.StartsWithSegments("/api/dispatch");

            if (!authenticated && (dispatchApi || IsBoard(path)))
            {
                if (dispatchApi)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { message = "Přihlaste se." });
                    return;
                }

                context.Response.Redirect("/login.html");
                return;
            }

            if (IsDispatchHost(host) && !authenticated && IsHome(path))
            {
                context.Response.Redirect("/login.html");
                return;
            }

            if (IsDispatchHost(host) && authenticated && (IsHome(path) || IsLogin(path)))
                context.Request.Path = "/dispecink.html";

            await next();
        });

        // Stejně jako /api/bi. RequireAuthorization() by skončilo prázdným 401
        // ještě před tímto middleware, protože UseAuthorization je registrované dřív.
        var api = app.MapGroup("/api/dispatch");
        api.MapGet("/board", GetBoard);
        api.MapGet("/trips", GetTrips);
        api.MapPost("/trips", CreateTrip);
        api.MapPost("/trips/{id}/assign", AssignTrip);
        api.MapPost("/trips/{id}/status", ChangeStatus);
    }

    private static Task<IResult> GetBoard(IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var snapshot = await store.ReadAsync(cancellationToken);
            return Results.Ok(ToBoard(snapshot));
        });

    private static Task<IResult> GetTrips(string? status, IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (!string.IsNullOrWhiteSpace(status) && !DispatchStatuses.IsKnown(status.Trim()))
                return Results.BadRequest(new { message = DispatchStatuses.UnknownMessage });

            var snapshot = await store.ReadAsync(cancellationToken);
            var trips = ToBoard(snapshot).Trips;
            if (!string.IsNullOrWhiteSpace(status))
            {
                var selected = status.Trim();
                trips = trips.Where(trip => trip.Status == selected).ToArray();
            }

            return Results.Ok(trips);
        });

    private static Task<IResult> CreateTrip(NewTripRequest request, IDispatchStore store, CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.AddTripAsync(request, cancellationToken)));

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

    private static bool IsDispatchHost(string host)
    {
        var name = host.Split(':')[0];
        return name.Equals("disp.phmtrans.cz", StringComparison.OrdinalIgnoreCase)
            || name.Equals("www.disp.phmtrans.cz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHome(string path) =>
        path is "" or "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

    private static bool IsLogin(string path) =>
        path.Equals("/login.html", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoard(string path) =>
        path.Equals("/dispecink.html", StringComparison.OrdinalIgnoreCase);

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
