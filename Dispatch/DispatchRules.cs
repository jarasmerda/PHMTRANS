namespace PHMTRANS.Dispatch;

public static class DispatchRules
{
    public static DispatchSnapshot Snapshot(DispatchDocument document) =>
        new(document.Trips.ToArray(), document.Vehicles.ToArray(), document.Drivers.ToArray());

    public static DispatchMutation AddTrip(DispatchDocument document, NewTripRequest request, DateTimeOffset now)
    {
        var error = ValidateNewTrip(request);
        if (error is not null)
            return new DispatchMutation.Rejected(error);

        var trip = new DispatchTrip(
            NewId(document.Trips),
            NextCode(document.Trips, now),
            Optional(request.OrderCode, 40),
            request.Customer!.Trim(),
            request.LoadingPlace!.Trim(),
            request.UnloadingPlace!.Trim(),
            Optional(request.Cargo, 80) ?? "",
            request.PlannedStart!.Value,
            DispatchStatuses.Nova,
            null,
            null,
            Optional(request.Note, 500),
            now);
        document.Trips.Add(trip);
        return new DispatchMutation.Success(trip);
    }

    public static DispatchMutation Assign(
        DispatchDocument document,
        string id,
        string? vehicleId,
        string? driverId,
        DateTimeOffset now)
    {
        vehicleId = BlankToNull(vehicleId);
        driverId = BlankToNull(driverId);

        var index = IndexOf(document, id);
        if (index < 0)
            return new DispatchMutation.NotFound();

        if (vehicleId is not null && document.Vehicles.All(vehicle => vehicle.Id != vehicleId))
            return new DispatchMutation.Rejected("Neznámé vozidlo.");
        if (driverId is not null && document.Drivers.All(driver => driver.Id != driverId))
            return new DispatchMutation.Rejected("Neznámý řidič.");

        var trip = document.Trips[index];
        var status = trip.Status;
        var complete = vehicleId is not null && driverId is not null;
        if (complete && status == DispatchStatuses.Nova)
            status = DispatchStatuses.Prirazena;
        else if (!complete && status == DispatchStatuses.Prirazena)
            status = DispatchStatuses.Nova;

        var updated = trip with
        {
            VehicleId = vehicleId,
            DriverId = driverId,
            Status = status,
            UpdatedAt = now
        };
        document.Trips[index] = updated;
        return new DispatchMutation.Success(updated);
    }

    public static DispatchMutation SetStatus(DispatchDocument document, string id, string? status, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(status))
            return new DispatchMutation.Rejected("Vyplňte stav.");

        var selected = status.Trim();
        if (!DispatchStatuses.IsKnown(selected))
            return new DispatchMutation.Rejected(DispatchStatuses.UnknownMessage);

        var index = IndexOf(document, id);
        if (index < 0)
            return new DispatchMutation.NotFound();

        var updated = document.Trips[index] with
        {
            Status = selected,
            UpdatedAt = now
        };
        document.Trips[index] = updated;
        return new DispatchMutation.Success(updated);
    }

    private static string? ValidateNewTrip(NewTripRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Customer))
            return "Vyplňte zákazníka.";
        if (request.Customer.Trim().Length > 160)
            return "Zákazník může mít nejvýše 160 znaků.";
        if (string.IsNullOrWhiteSpace(request.LoadingPlace))
            return "Vyplňte místo nakládky.";
        if (request.LoadingPlace.Trim().Length > 160)
            return "Místo nakládky může mít nejvýše 160 znaků.";
        if (string.IsNullOrWhiteSpace(request.UnloadingPlace))
            return "Vyplňte místo vykládky.";
        if (request.UnloadingPlace.Trim().Length > 160)
            return "Místo vykládky může mít nejvýše 160 znaků.";
        if (Optional(request.Cargo, 80) is null && !string.IsNullOrWhiteSpace(request.Cargo))
            return "Náklad může mít nejvýše 80 znaků.";
        if (Optional(request.OrderCode, 40) is null && !string.IsNullOrWhiteSpace(request.OrderCode))
            return "Zakázka může mít nejvýše 40 znaků.";
        if (Optional(request.Note, 500) is null && !string.IsNullOrWhiteSpace(request.Note))
            return "Poznámka může mít nejvýše 500 znaků.";
        if (request.PlannedStart is null)
            return "Vyplňte termín jízdy.";
        return null;
    }

    private static string? Optional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : null;
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int IndexOf(DispatchDocument document, string id) =>
        document.Trips.FindIndex(trip => trip.Id == id);

    private static string NewId(IReadOnlyList<DispatchTrip> trips)
    {
        var id = Guid.NewGuid().ToString("n");
        while (trips.Any(trip => trip.Id == id))
            id = Guid.NewGuid().ToString("n");
        return id;
    }

    private static string NextCode(IReadOnlyList<DispatchTrip> trips, DateTimeOffset now)
    {
        var prefix = $"J{now.Year}-";
        var highest = 0;
        foreach (var trip in trips)
        {
            if (!trip.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(trip.Code.AsSpan(prefix.Length), out var number))
                highest = Math.Max(highest, number);
        }

        return $"{prefix}{(highest + 1):0000}";
    }
}
