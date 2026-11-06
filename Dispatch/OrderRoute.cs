using System.Text.Json;

namespace PHMTRANS.Dispatch;

public sealed record OrderStop(
    string Id,
    int Sequence,
    string Place,
    decimal Liters,
    string? Note,
    DateTimeOffset? ArrivalAt,
    int? FuelTypeId = null,
    string? FuelType = null);

public sealed record OrderLeg(
    string Id,
    int Sequence,
    string FromPlace,
    string ToPlace,
    decimal Liters,
    decimal? Kilometers,
    decimal SalePrice,
    bool SaleOverridden,
    decimal CostPrice,
    string? KmSource = null);

public sealed record OrderStopInput(
    string? Place,
    decimal? Liters,
    string? Note,
    DateTimeOffset? ArrivalAt,
    int? FuelTypeId = null);

public sealed record OrderLegInput(
    decimal? Kilometers,
    decimal? SalePrice,
    bool? SaleOverridden,
    string? KmSource = null);

public sealed record OrderRouteSnapshot(
    string PricingMode,
    decimal SalePerKm,
    decimal SalePerLiter,
    decimal CostPerKm,
    IReadOnlyList<OrderStop> Stops,
    IReadOnlyList<OrderLeg> Legs);

public sealed record TripLink(string Id, string? VehicleId, string? DriverId, string? Status = null);

public static class OrderCatalogs
{
    public static OrderCatalog From(DispatchSnapshot snapshot) =>
        From(snapshot, snapshot.DefaultCostPerKm);

    public static OrderCatalog From(DispatchSnapshot snapshot, decimal defaultCostPerKm)
    {
        var trips = snapshot.Trips.ToDictionary(
            trip => trip.Id,
            trip => new TripLink(trip.Id, trip.VehicleId, trip.DriverId, trip.Status),
            StringComparer.Ordinal);
        var rates = snapshot.Vehicles
            .Where(vehicle => vehicle.CostPerKm is not null)
            .ToDictionary(vehicle => vehicle.Id, vehicle => vehicle.CostPerKm!.Value, StringComparer.Ordinal);
        return new OrderCatalog(
            snapshot.Vehicles.Select(vehicle => vehicle.Id).ToHashSet(StringComparer.Ordinal),
            snapshot.Drivers.Select(driver => driver.Id).ToHashSet(StringComparer.Ordinal),
            trips.Keys.ToHashSet(StringComparer.Ordinal),
            trips,
            defaultCostPerKm,
            rates);
    }
}

public readonly record struct RouteBuild(DispatchOrder? Order, string? Error, IReadOnlyList<DistanceWrite>? Distances = null)
{
    public static RouteBuild Ok(DispatchOrder order, IReadOnlyList<DistanceWrite>? distances = null) => new(order, null, distances);

    public static RouteBuild Fail(string error) => new(null, error);
}

public static class OrderRoute
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Write(DispatchOrder order)
    {
        var snapshot = new OrderRouteSnapshot(
            order.PricingMode,
            order.SalePerKm,
            order.SalePerLiter,
            order.CostPerKm,
            order.Stops ?? [],
            order.Legs ?? []);
        return JsonSerializer.Serialize(snapshot, Json);
    }

    public static DispatchOrder Read(DispatchOrder order, string? routeJson)
    {
        OrderRouteSnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(routeJson))
        {
            try
            {
                snapshot = JsonSerializer.Deserialize<OrderRouteSnapshot>(routeJson, Json);
            }
            catch (JsonException)
            {
                snapshot = null;
            }
        }

        if (snapshot is null || snapshot.Stops is null || snapshot.Stops.Count == 0)
            snapshot = Legacy(order.LoadingPlace, order.UnloadingPlace, order.Liters, order.AgreedPrice);

        var routed = order with
        {
            PricingMode = OrderPricing.IsKnown(snapshot.PricingMode) ? snapshot.PricingMode : order.PricingMode,
            SalePerKm = snapshot.SalePerKm,
            SalePerLiter = snapshot.SalePerLiter,
            CostPerKm = snapshot.CostPerKm,
            Stops = snapshot.Stops,
            Legs = snapshot.Legs
        };
        return routed with { Quote = OrderPricing.Summarize(routed) };
    }

    public static OrderRouteSnapshot Legacy(string loadingPlace, string unloadingPlace, decimal liters, decimal agreedPrice)
    {
        var stop = new OrderStop(NewId(), 1, unloadingPlace, liters, null, null);
        var leg = new OrderLeg(NewId(), 1, loadingPlace, unloadingPlace, liters, null, OrderPohodaFields.Money(agreedPrice), true, 0m);
        return new OrderRouteSnapshot(OrderPricing.ByKm, 0m, 0m, 0m, [stop], [leg]);
    }

    public static RouteBuild Materialize(
        DispatchOrder built,
        OrderSaveRequest request,
        OrderCatalog catalog,
        DispatchOrder? previous = null,
        IReadOnlyDictionary<string, decimal>? distances = null)
    {
        var mode = NormalizeMode(string.IsNullOrWhiteSpace(request.PricingMode)
            ? previous?.PricingMode ?? OrderPricing.ByKm
            : request.PricingMode.Trim());
        if (!OrderPricing.IsKnown(mode))
            return RouteBuild.Fail("Režim ceny je Km nebo Litry.");

        var salePerKm = Rate(request.SalePerKm, previous?.SalePerKm ?? 0m, "Prodejní sazba za km je mimo povolený rozsah.");
        if (salePerKm.Error is not null)
            return RouteBuild.Fail(salePerKm.Error);
        var salePerLiter = Rate(request.SalePerLiter, previous?.SalePerLiter ?? 0m, "Prodejní sazba za litr je mimo povolený rozsah.");
        if (salePerLiter.Error is not null)
            return RouteBuild.Fail(salePerLiter.Error);
        var costPerKm = Rate(request.CostPerKm, DefaultCost(built, previous, catalog), "Nákladová sazba za km je mimo povolený rozsah.");
        if (costPerKm.Error is not null)
            return RouteBuild.Fail(costPerKm.Error);

        var crew = Crew(built.TripId, built.VehicleId, built.DriverId, catalog);
        OrderRouteSnapshot snapshot;
        IReadOnlyList<DistanceWrite>? distanceWrites = null;
        if (request.Stops is null)
        {
            snapshot = Legacy(built.LoadingPlace, built.UnloadingPlace, built.Liters, built.AgreedPrice);
        }
        else
        {
            var derived = BuildStops(
                built.LoadingPlace,
                request.Stops,
                request.Legs,
                mode,
                salePerKm.Value,
                salePerLiter.Value,
                costPerKm.Value,
                built.FuelTypeId,
                built.FuelType,
                catalog.FuelTypes,
                distances);
            if (derived.Error is not null)
                return RouteBuild.Fail(derived.Error);
            snapshot = derived.Snapshot!;
            distanceWrites = derived.Writes;
        }
        var unloading = snapshot.Stops[^1].Place;
        var agreed = OrderPohodaFields.Money(snapshot.Legs.Sum(leg => leg.SalePrice));
        var order = built with
        {
            UnloadingPlace = unloading,
            AgreedPrice = agreed,
            VehicleId = crew.VehicleId,
            DriverId = crew.DriverId,
            PricingMode = mode,
            SalePerKm = salePerKm.Value,
            SalePerLiter = salePerLiter.Value,
            CostPerKm = costPerKm.Value,
            Stops = snapshot.Stops,
            Legs = snapshot.Legs
        };
        return RouteBuild.Ok(order with { Quote = OrderPricing.Summarize(order) }, distanceWrites);
    }

    public static (string? VehicleId, string? DriverId) Crew(string? tripId, string? vehicleId, string? driverId, OrderCatalog catalog)
    {
        if (tripId is not null && catalog.Trips is not null && catalog.Trips.TryGetValue(tripId, out var trip))
            return (trip.VehicleId, trip.DriverId);
        return (vehicleId, driverId);
    }

    public static DispatchOrder Display(DispatchOrder order, OrderCatalog catalog)
    {
        var crew = Crew(order.TripId, order.VehicleId, order.DriverId, catalog);
        var shown = order with { VehicleId = crew.VehicleId, DriverId = crew.DriverId };
        return shown.Quote is null ? shown with { Quote = OrderPricing.Summarize(shown) } : shown;
    }

    private static string NormalizeMode(string mode)
    {
        if (mode.Equals(OrderPricing.ByKm, StringComparison.OrdinalIgnoreCase))
            return OrderPricing.ByKm;
        if (mode.Equals(OrderPricing.ByLiters, StringComparison.OrdinalIgnoreCase)
            || mode.Equals("Litry", StringComparison.OrdinalIgnoreCase))
            return OrderPricing.ByLiters;
        return mode;
    }

    private static decimal DefaultCost(DispatchOrder built, DispatchOrder? previous, OrderCatalog catalog)
    {
        if (previous is not null)
            return previous.CostPerKm;
        if (built.TripId is not null
            && catalog.Trips is not null
            && catalog.Trips.TryGetValue(built.TripId, out var trip)
            && trip.VehicleId is not null
            && catalog.VehicleCostPerKm is not null
            && catalog.VehicleCostPerKm.TryGetValue(trip.VehicleId, out var vehicleRate))
            return vehicleRate;
        return catalog.DefaultCostPerKm;
    }

    private static (decimal Value, string? Error) Rate(decimal? requested, decimal fallback, string message)
    {
        var value = OrderPricing.Rate(requested ?? fallback);
        if (value is < 0m or > 1_000_000m)
            return (0m, message);
        return (value, null);
    }

    private static (OrderRouteSnapshot? Snapshot, string? Error, IReadOnlyList<DistanceWrite>? Writes) BuildStops(
        string loadingPlace,
        IReadOnlyList<OrderStopInput> stops,
        IReadOnlyList<OrderLegInput>? legs,
        string mode,
        decimal salePerKm,
        decimal salePerLiter,
        decimal costPerKm,
        int? headerFuelId,
        string headerFuel,
        IReadOnlyList<FuelTypeItem>? fuelTypes,
        IReadOnlyDictionary<string, decimal>? distances)
    {
        if (stops.Count == 0)
            return (null, "Přidejte aspoň jednu vykládku.", null);
        if (stops.Count > 30)
            return (null, "Vykládek může být nejvýše 30.", null);

        var builtStops = new List<OrderStop>(stops.Count);
        var builtLegs = new List<OrderLeg>(stops.Count);
        var writes = new List<DistanceWrite>();
        for (var index = 0; index < stops.Count; index++)
        {
            var input = stops[index];
            if (string.IsNullOrWhiteSpace(input.Place))
                return (null, "Vyplňte místo vykládky.", null);
            var place = input.Place.Trim();
            if (place.Length > 160)
                return (null, "Místo vykládky může mít nejvýše 160 znaků.", null);
            if (input.Liters is null)
                return (null, "Vyplňte litry na vykládce.", null);
            if (input.Liters is < 0m or > 1_000_000m)
                return (null, "Litry na vykládce musí být od 0 do 1 000 000.", null);
            if (!string.IsNullOrWhiteSpace(input.Note) && input.Note.Trim().Length > 500)
                return (null, "Poznámka vykládky může mít nejvýše 500 znaků.", null);

            var fuel = StopFuel(input.FuelTypeId, headerFuelId, headerFuel, fuelTypes);
            if (fuel.Error is not null)
                return (null, fuel.Error, null);

            var liters = OrderPricing.Liters(input.Liters.Value);
            var from = index == 0 ? loadingPlace : builtStops[index - 1].Place;
            var legInput = legs is not null && index < legs.Count ? legs[index] : null;
            decimal? kilometers = null;
            if (legInput?.Kilometers is not null)
            {
                if (legInput.Kilometers is < 0m or > 100_000m)
                    return (null, "Kilometry úseku musí být od 0 do 100 000.", null);
                kilometers = OrderPricing.Kilometers(legInput.Kilometers.Value);
            }

            var pair = PlacePair.Canonical(from, place);
            decimal? known = null;
            if (pair is not null && distances is not null && distances.TryGetValue(PlacePair.Key(pair.Value.From, pair.Value.To), out var found))
                known = found;
            var decision = distances is null
                ? new DistanceDecision(kilometers, null, false)
                : DistanceChoice.Resolve(kilometers, legInput?.KmSource, known);
            if (decision.Upsert && pair is not null && decision.Kilometers is not null)
                writes.Add(new DistanceWrite(pair.Value.From, pair.Value.To, decision.Kilometers.Value));
            kilometers = decision.Kilometers;

            var overridden = legInput?.SaleOverridden == true;
            var overridePrice = legInput?.SalePrice ?? 0m;
            if (overridden && overridePrice is < 0m or > 1_000_000_000m)
                return (null, "Cena úseku je mimo povolený rozsah.", null);

            var sequence = index + 1;
            builtStops.Add(new OrderStop(
                NewId(),
                sequence,
                place,
                liters,
                string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
                input.ArrivalAt,
                fuel.Id,
                fuel.Name));
            builtLegs.Add(new OrderLeg(
                NewId(),
                sequence,
                from,
                place,
                liters,
                kilometers,
                OrderPricing.Sale(kilometers, liters, overridden, overridePrice, mode, salePerKm, salePerLiter),
                overridden,
                OrderPricing.Cost(kilometers, costPerKm),
                decision.Source));
        }

        return (new OrderRouteSnapshot(mode, salePerKm, salePerLiter, costPerKm, builtStops, builtLegs), null, writes);
    }

    private static (int? Id, string? Name, string? Error) StopFuel(
        int? requestedId,
        int? headerId,
        string headerName,
        IReadOnlyList<FuelTypeItem>? fuelTypes)
    {
        var id = requestedId ?? headerId;
        if (fuelTypes is not { Count: > 0 })
            return (id, null, null);

        var match = FuelTypeCatalog.Find(fuelTypes, id, id is null ? headerName : null);
        return match is null
            ? (null, null, "Vyberte druh paliva na vykládce.")
            : (match.Id, match.Name, null);
    }

    private static string NewId() => Guid.NewGuid().ToString("n");
}

public static class OrderAssignment
{
    public const string TakenMessage = "Zakázka už je na jiné jízdě.";

    public static string? Check(DispatchOrder? order, string? tripId, bool onlyIfFree, OrderCatalog catalog)
    {
        if (order is null)
            return "Zakázka neexistuje.";
        if (order.Status == OrderStatuses.Stornovano)
            return "Stornovaná zakázka je jen ke čtení.";
        if (tripId is not null && !catalog.TripIds.Contains(tripId))
            return "Neznámá jízda.";
        if (tripId is not null
            && catalog.Trips is not null
            && catalog.Trips.TryGetValue(tripId, out var link)
            && link.Status == DispatchStatuses.Stornovano)
            return "Stornovaná jízda už zakázky nepřijímá.";
        if (onlyIfFree && tripId is not null && order.TripId is not null && !string.Equals(order.TripId, tripId, StringComparison.Ordinal))
            return TakenMessage;
        return null;
    }
}
