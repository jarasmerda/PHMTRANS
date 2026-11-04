namespace PHMTRANS.Dispatch;

public static class DispatchRules
{
    public static DispatchSnapshot Snapshot(DispatchDocument document) =>
        new(document.Trips.ToArray(), document.Vehicles.ToArray(), document.Drivers.ToArray(), document.DefaultCostPerKm);

    public static DispatchMutation AddTrip(DispatchDocument document, NewTripRequest request, DateTimeOffset now)
    {
        var error = ValidateNewTrip(request);
        if (error is not null)
            return new DispatchMutation.Rejected(error);

        var vehicleId = BlankToNull(request.VehicleId);
        var driverId = BlankToNull(request.DriverId);
        var assignmentError = ValidateAssignment(document, vehicleId, driverId);
        if (assignmentError is not null)
            return new DispatchMutation.Rejected(assignmentError);

        var trip = new DispatchTrip(
            NewId(document.Trips),
            NextCode(document.Trips, now),
            request.Customer!.Trim(),
            request.LoadingPlace!.Trim(),
            request.UnloadingPlace!.Trim(),
            Optional(request.Cargo, 80) ?? "",
            request.PlannedStart!.Value,
            StatusAfterAssignment(DispatchStatuses.Nova, vehicleId, driverId),
            vehicleId,
            driverId,
            Optional(request.Note, 500),
            now);
        document.Trips.Add(trip);
        return new DispatchMutation.Success(trip);
    }

    public static DispatchMutation UpdateTrip(DispatchDocument document, string id, UpdateTripRequest request, DateTimeOffset now)
    {
        var index = IndexOf(document, id);
        if (index < 0)
            return new DispatchMutation.NotFound();
        if (document.Trips[index].Status == DispatchStatuses.Stornovano)
            return new DispatchMutation.Rejected("Stornovaná jízda je jen ke čtení.");

        var error = ValidateTripFields(
            request.Customer,
            request.LoadingPlace,
            request.UnloadingPlace,
            request.Cargo,
            request.Note,
            request.PlannedStart);
        if (error is not null)
            return new DispatchMutation.Rejected(error);

        var vehicleId = BlankToNull(request.VehicleId);
        var driverId = BlankToNull(request.DriverId);
        var assignmentError = ValidateAssignment(document, vehicleId, driverId);
        if (assignmentError is not null)
            return new DispatchMutation.Rejected(assignmentError);

        var current = document.Trips[index];
        if (!TryResolveStatus(current.Status, request.Status, vehicleId, driverId, out var status, out var statusError))
            return new DispatchMutation.Rejected(statusError!);

        var updated = current with
        {
            Customer = request.Customer!.Trim(),
            LoadingPlace = request.LoadingPlace!.Trim(),
            UnloadingPlace = request.UnloadingPlace!.Trim(),
            Cargo = Optional(request.Cargo, 80) ?? "",
            PlannedStart = request.PlannedStart!.Value,
            Status = status,
            VehicleId = vehicleId,
            DriverId = driverId,
            Note = Optional(request.Note, 500),
            UpdatedAt = now
        };
        document.Trips[index] = updated;
        return new DispatchMutation.Success(updated);
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
        if (document.Trips[index].Status == DispatchStatuses.Stornovano)
            return new DispatchMutation.Rejected("Stornovaná jízda je jen ke čtení.");

        var assignmentError = ValidateAssignment(document, vehicleId, driverId);
        if (assignmentError is not null)
            return new DispatchMutation.Rejected(assignmentError);

        var trip = document.Trips[index];
        var updated = trip with
        {
            VehicleId = vehicleId,
            DriverId = driverId,
            Status = StatusAfterAssignment(trip.Status, vehicleId, driverId),
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
        if (document.Trips[index].Status == DispatchStatuses.Stornovano)
            return new DispatchMutation.Rejected("Stornovaná jízda je jen ke čtení.");

        var updated = document.Trips[index] with
        {
            Status = selected,
            UpdatedAt = now
        };
        document.Trips[index] = updated;
        return new DispatchMutation.Success(updated);
    }

    public static bool CanHardDelete(DispatchTrip trip, int orderCount) =>
        orderCount == 0
        && trip.Status == DispatchStatuses.Nova
        && trip.VehicleId is null
        && trip.DriverId is null;

    public static DispatchMutation CancelTrip(DispatchDocument document, string id, DateTimeOffset now)
    {
        var index = IndexOf(document, id);
        if (index < 0)
            return new DispatchMutation.NotFound();
        if (document.Trips[index].Status == DispatchStatuses.Stornovano)
            return new DispatchMutation.Rejected("Jízda už je stornovaná.");

        var updated = document.Trips[index] with
        {
            Status = DispatchStatuses.Stornovano,
            UpdatedAt = now
        };
        document.Trips[index] = updated;
        return new DispatchMutation.Success(updated);
    }

    public static DispatchMutation DeleteTrip(DispatchDocument document, string id, int orderCount)
    {
        var index = IndexOf(document, id);
        if (index < 0)
            return new DispatchMutation.NotFound();

        var trip = document.Trips[index];
        if (trip.Status == DispatchStatuses.Stornovano)
            return new DispatchMutation.Rejected("Stornovaná jízda zůstává v historii.");
        if (!CanHardDelete(trip, orderCount))
            return new DispatchMutation.Rejected("Jízdu s historií nebo se zakázkami lze jen stornovat.");

        document.Trips.RemoveAt(index);
        return new DispatchMutation.Deleted(id);
    }

    public static DispatchRateCard Rates(DispatchDocument document) =>
        new(document.DefaultCostPerKm, document.Vehicles.ToArray());

    public static string? SaveRates(DispatchDocument document, decimal defaultCostPerKm, IReadOnlyList<VehicleCostUpdate> vehicles)
    {
        if (defaultCostPerKm is < 0m or > 1_000_000m)
            return "Nákladová sazba je mimo povolený rozsah.";

        var updates = new List<(int Index, decimal? Cost)>();
        foreach (var vehicle in vehicles)
        {
            if (string.IsNullOrWhiteSpace(vehicle.Id))
                return "Neznámé vozidlo.";
            var index = document.Vehicles.FindIndex(item => item.Id == vehicle.Id);
            if (index < 0)
                return "Neznámé vozidlo.";
            if (vehicle.CostPerKm is < 0m or > 1_000_000m)
                return "Nákladová sazba je mimo povolený rozsah.";
            updates.Add((index, vehicle.CostPerKm is null ? null : OrderPricing.Rate(vehicle.CostPerKm.Value)));
        }

        document.DefaultCostPerKm = OrderPricing.Rate(defaultCostPerKm);
        foreach (var update in updates)
        {
            var current = document.Vehicles[update.Index];
            document.Vehicles[update.Index] = current with { CostPerKm = update.Cost };
        }

        return null;
    }

    private static string? ValidateAssignment(DispatchDocument document, string? vehicleId, string? driverId)
    {
        if (vehicleId is not null && document.Vehicles.All(vehicle => vehicle.Id != vehicleId))
            return "Neznámé vozidlo.";
        if (driverId is not null && document.Drivers.All(driver => driver.Id != driverId))
            return "Neznámý řidič.";
        return null;
    }

    private static string StatusAfterAssignment(string status, string? vehicleId, string? driverId)
    {
        var complete = vehicleId is not null && driverId is not null;
        if (complete && status == DispatchStatuses.Nova)
            return DispatchStatuses.Prirazena;
        if (!complete && status == DispatchStatuses.Prirazena)
            return DispatchStatuses.Nova;
        return status;
    }

    private static string? ValidateNewTrip(NewTripRequest request) =>
        ValidateTripFields(
            request.Customer,
            request.LoadingPlace,
            request.UnloadingPlace,
            request.Cargo,
            request.Note,
            request.PlannedStart);

    private static string? ValidateTripFields(
        string? customer,
        string? loadingPlace,
        string? unloadingPlace,
        string? cargo,
        string? note,
        DateTimeOffset? plannedStart)
    {
        if (string.IsNullOrWhiteSpace(customer))
            return "Vyplňte zákazníka.";
        if (customer.Trim().Length > 160)
            return "Zákazník může mít nejvýše 160 znaků.";
        if (string.IsNullOrWhiteSpace(loadingPlace))
            return "Vyplňte místo nakládky.";
        if (loadingPlace.Trim().Length > 160)
            return "Místo nakládky může mít nejvýše 160 znaků.";
        if (string.IsNullOrWhiteSpace(unloadingPlace))
            return "Vyplňte místo vykládky.";
        if (unloadingPlace.Trim().Length > 160)
            return "Místo vykládky může mít nejvýše 160 znaků.";
        if (Optional(cargo, 80) is null && !string.IsNullOrWhiteSpace(cargo))
            return "Náklad může mít nejvýše 80 znaků.";
        if (Optional(note, 500) is null && !string.IsNullOrWhiteSpace(note))
            return "Poznámka může mít nejvýše 500 znaků.";
        if (plannedStart is null)
            return "Vyplňte termín jízdy.";
        return null;
    }

    private static bool TryResolveStatus(
        string current,
        string? requested,
        string? vehicleId,
        string? driverId,
        out string status,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested.Trim(), current, StringComparison.Ordinal))
        {
            status = StatusAfterAssignment(current, vehicleId, driverId);
            error = null;
            return true;
        }

        var selected = requested.Trim();
        if (selected == DispatchStatuses.Stornovano)
        {
            status = current;
            error = "Jízdu stornujte tlačítkem Stornovat. Tím se odeberou zakázky.";
            return false;
        }

        if (!DispatchStatuses.IsKnown(selected))
        {
            status = current;
            error = DispatchStatuses.UnknownMessage;
            return false;
        }

        status = selected;
        error = null;
        return true;
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
