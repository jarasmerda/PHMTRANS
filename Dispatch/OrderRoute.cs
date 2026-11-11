using System.Globalization;
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
    string? FuelType = null,
    IReadOnlyList<OrderStopItem>? Items = null);

// Jedno palivo vykládky: ze kterých přepážek, jaké palivo a kolik litrů. Vykládka jich může mít víc.
// Compartment = první přepážka (starší zápis), Compartments = všechny přepážky řádku.
public sealed record OrderStopItem(int? FuelTypeId, string FuelType, int? Compartment, decimal Liters, IReadOnlyList<int>? Compartments = null);

public sealed record OrderStopItemInput(int? FuelTypeId, int? Compartment, decimal? Liters, IReadOnlyList<int>? Compartments = null);

public static class OrderStopItems
{
    public const int MaxCount = 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Paliva vykládky. Stará vykládka bez řádků má jedno palivo bez přepážky.
    public static IReadOnlyList<OrderStopItem> Of(OrderStop stop) =>
        stop.Items is { Count: > 0 }
            ? stop.Items
            : [new OrderStopItem(stop.FuelTypeId, stop.FuelType ?? "", null, stop.Liters)];

    public static string FuelText(OrderStop stop) =>
        string.Join(" + ", Of(stop).Select(item => item.FuelType.Trim()).Where(name => name.Length > 0).Distinct());

    // Přepážky řádku vykládky (nové i starší zápisy).
    public static IReadOnlyList<int> Numbers(OrderStopItem item) =>
        item.Compartments is { Count: > 0 } ? item.Compartments
        : item.Compartment is { } number ? [number]
        : [];

    public static bool Same(OrderStop left, OrderStop right) =>
        Of(left).Select(Key).SequenceEqual(Of(right).Select(Key));

    private static string Key(OrderStopItem item) =>
        $"{item.FuelTypeId}|{item.FuelType}|{item.Liters}|{string.Join(",", Numbers(item))}";

    public static string? Write(IReadOnlyList<OrderStopItem>? items) =>
        items is { Count: > 0 } ? JsonSerializer.Serialize(items, Json) : null;

    public static IReadOnlyList<OrderStopItem>? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var items = JsonSerializer.Deserialize<OrderStopItem[]>(json, Json);
            return items is { Length: > 0 } ? items : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

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
    string? KmSource = null,
    decimal? BilledKilometers = null);

public sealed record OrderStopInput(
    string? Place,
    decimal? Liters,
    string? Note,
    DateTimeOffset? ArrivalAt,
    int? FuelTypeId = null,
    IReadOnlyList<OrderStopItemInput>? Items = null);

// Nakládka jednoho paliva. Zakázka jich může mít víc (nafta, benzín …).
// Compartments = čísla přepážek vozidla, do kterých se palivo nalévá (nepovinné).
public sealed record OrderLoad(int Sequence, int? FuelTypeId, string FuelType, decimal Liters, IReadOnlyList<int>? Compartments = null);

public sealed record OrderLoadInput(int? FuelTypeId, decimal? Liters, IReadOnlyList<int>? Compartments = null);

public sealed record FuelBalance(int? FuelTypeId, string FuelType, decimal Loaded, decimal Delivered);

public static class OrderLoads
{
    public const int MaxCount = 10;
    private static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs-CZ");

    public static IReadOnlyList<OrderLoad> Effective(DispatchOrder order) =>
        order.Loads is { Count: > 0 }
            ? order.Loads
            : [new OrderLoad(1, order.FuelTypeId, order.FuelType, order.Liters)];

    public static (IReadOnlyList<OrderLoad>? Loads, string? Error) Build(
        IReadOnlyList<OrderLoadInput> inputs,
        IReadOnlyList<FuelTypeItem>? fuelTypes)
    {
        if (inputs.Count == 0)
            return (null, "Přidejte aspoň jedno palivo do nakládky.");
        if (inputs.Count > MaxCount)
            return (null, "Nakládka může mít nejvýše 10 paliv.");
        if (fuelTypes is not { Count: > 0 })
            return (null, "Číselník paliv je prázdný.");

        var loads = new List<OrderLoad>(inputs.Count);
        var used = new Dictionary<int, string>();
        foreach (var input in inputs)
        {
            var fuel = input.FuelTypeId is null ? null : FuelTypeCatalog.Find(fuelTypes, input.FuelTypeId, null);
            if (fuel is null)
                return (null, "Vyberte druh paliva v nakládce.");
            if (input.Liters is null)
                return (null, $"Vyplňte naložené litry ({fuel.Name}).");
            if (input.Liters is < 0m or > 1_000_000m)
                return (null, "Naložené litry musí být od 0 do 1 000 000.");
            if (loads.Any(load => load.FuelTypeId == fuel.Id))
                return (null, $"Palivo {fuel.Name} je v nakládce dvakrát. Litry sečtěte do jednoho řádku.");
            int[]? compartments = null;
            if (input.Compartments is { Count: > 0 })
            {
                if (input.Compartments.Any(number => number is < 1 or > VehicleCompartments.MaxCount))
                    return (null, $"Přepážky se číslují 1 až {VehicleCompartments.MaxCount}.");
                compartments = input.Compartments.Distinct().Order().ToArray();
                foreach (var number in compartments)
                {
                    if (used.TryGetValue(number, out var other))
                        return (null, $"Přepážka {number} je u dvou paliv ({other} a {fuel.Name}). Jedna přepážka může mít jen jedno palivo.");
                    used[number] = fuel.Name;
                }
            }

            loads.Add(new OrderLoad(loads.Count + 1, fuel.Id, fuel.Name, OrderPricing.Liters(input.Liters.Value), compartments));
        }

        return (loads, null);
    }

    // Přepážky musí vozidlo mít a naložené litry se do nich musí vejít.
    public static string? CheckCompartments(IReadOnlyList<OrderLoad>? loads, string? vehicleId, OrderCatalog catalog)
    {
        var assigned = (loads ?? []).Where(load => load.Compartments is { Count: > 0 }).ToArray();
        if (assigned.Length == 0)
            return null;
        if (vehicleId is null)
            return "Přepážky patří k vozidlu. Vyberte vozidlo, nebo přepážky u nakládky zrušte.";
        if (catalog.VehicleCompartments is null || !catalog.VehicleCompartments.TryGetValue(vehicleId, out var available) || available.Count == 0)
            return "Vybrané vozidlo nemá zadané přepážky. Doplňte je u vozidla, nebo přepážky u nakládky zrušte.";

        foreach (var load in assigned)
        {
            var capacity = 0m;
            foreach (var number in load.Compartments!)
            {
                var compartment = available.FirstOrDefault(item => item.Number == number);
                if (compartment is null)
                    return $"Vozidlo nemá přepážku {number}.";
                capacity += compartment.CapacityLiters;
            }

            if (load.Liters > capacity)
                return $"{load.FuelType}: {load.Liters.ToString("N0", Czech)} l se nevejde do přepážek {string.Join(", ", load.Compartments!)} (dohromady {capacity.ToString("N0", Czech)} l).";
        }

        // Když se přepážky přiřazují, musí je mít každé naložené palivo.
        var missing = (loads ?? []).FirstOrDefault(load => load.Liters > 0m && load.Compartments is not { Count: > 0 });
        if (missing is not null)
        {
            var taken = assigned.SelectMany(load => load.Compartments!).ToHashSet();
            var free = available.Count(item => !taken.Contains(item.Number));
            return free == 0
                ? $"{missing.FuelType}: {missing.Liters.ToString("N0", Czech)} l nemá kam. Všechny přepážky vozidla už mají jiné palivo."
                : $"{missing.FuelType}: vyberte přepážku. Když se přepážky přiřazují, musí je mít každé palivo.";
        }

        return null;
    }

    // Vykládka: každé palivo se vykládá z přepážky, ve které to palivo je (když se přepážky přiřazují).
    public static string? CheckUnloadCompartments(DispatchOrder order, OrderCatalog catalog)
    {
        var owners = new Dictionary<int, OrderLoad>();
        foreach (var load in Effective(order))
            foreach (var number in load.Compartments ?? [])
                owners[number] = load;
        IReadOnlyList<VehicleCompartment> available = [];
        if (order.VehicleId is not null && catalog.VehicleCompartments is not null
            && catalog.VehicleCompartments.TryGetValue(order.VehicleId, out var list))
            available = list;

        var groups = new List<(IReadOnlyList<int> Numbers, decimal Liters)>();
        foreach (var stop in order.Stops ?? [])
        {
            var label = $"Vykládka {stop.Sequence} ({stop.Place})";
            foreach (var item in OrderStopItems.Of(stop))
            {
                var numbers = OrderStopItems.Numbers(item);
                if (numbers.Count == 0)
                {
                    if (owners.Count > 0 && item.Liters > 0m)
                        return $"{label}: u paliva {item.FuelType} vyberte přepážku, ze které se vykládá.";
                    continue;
                }

                if (owners.Count == 0)
                    return $"{label}: přepážku vyberte až po přiřazení přepážek u nakládky.";
                foreach (var number in numbers)
                {
                    if (!owners.TryGetValue(number, out var owner))
                        return $"{label}: přepážka {number} se v nakládce neplní.";
                    var sameFuel = owner.FuelTypeId is not null && item.FuelTypeId is not null
                        ? owner.FuelTypeId == item.FuelTypeId
                        : string.Equals(owner.FuelType.Trim(), item.FuelType.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (!sameFuel)
                        return $"{label}: v přepážce {number} je {owner.FuelType}, ne {item.FuelType}.";
                }

                groups.Add((numbers, item.Liters));
            }
        }

        // Z přepážek se nedá vyložit víc, než pojmou. Řádky, které sdílejí přepážku, se počítají dohromady.
        var parent = new Dictionary<int, int>();
        int Root(int number)
        {
            while (parent.TryGetValue(number, out var up) && up != number)
                number = up;
            return number;
        }
        foreach (var (numbers, _) in groups)
        {
            foreach (var number in numbers)
                parent.TryAdd(number, number);
            for (var index = 1; index < numbers.Count; index++)
                parent[Root(numbers[index])] = Root(numbers[0]);
        }
        foreach (var component in groups.GroupBy(group => Root(group.Numbers[0])).OrderBy(group => group.Key))
        {
            var members = parent.Keys.Where(number => Root(number) == component.Key).Order().ToArray();
            var liters = component.Sum(group => group.Liters);
            var capacities = members.Select(number => available.FirstOrDefault(item => item.Number == number)?.CapacityLiters).ToArray();
            if (capacities.Any(capacity => capacity is null))
                continue;
            var capacity = capacities.Sum(value => value!.Value);
            if (liters > capacity)
                return members.Length == 1
                    ? $"Z přepážky {members[0]} se vykládá {liters.ToString("N0", Czech)} l, ale pojme jen {capacity.ToString("N0", Czech)} l."
                    : $"Z přepážek {string.Join(", ", members)} se vykládá {liters.ToString("N0", Czech)} l, ale pojmou jen {capacity.ToString("N0", Czech)} l.";
        }

        return null;
    }

    public static string CompartmentsText(IReadOnlyList<int>? compartments) =>
        compartments is { Count: > 0 } ? string.Join(",", compartments) : "";

    public static IReadOnlyList<int>? ParseCompartments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var numbers = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .Where(number => number is >= 1 and <= VehicleCompartments.MaxCount)
            .Distinct()
            .Order()
            .ToArray();
        return numbers.Length > 0 ? numbers : null;
    }

    // Naloženo a vyloženo po palivech. Vykládka bez paliva patří k prvnímu palivu nakládky.
    public static IReadOnlyList<FuelBalance> Balance(DispatchOrder order)
    {
        var loads = Effective(order);
        var first = loads[0];
        var rows = new List<FuelBalance>();
        foreach (var load in loads)
        {
            var index = rows.FindIndex(row => Same(row.FuelTypeId, row.FuelType, load.FuelTypeId, load.FuelType));
            if (index < 0)
                rows.Add(new FuelBalance(load.FuelTypeId, load.FuelType, load.Liters, 0m));
            else
                rows[index] = rows[index] with { Loaded = rows[index].Loaded + load.Liters };
        }

        foreach (var item in (order.Stops ?? []).SelectMany(OrderStopItems.Of))
        {
            var id = item.FuelTypeId ?? (string.IsNullOrWhiteSpace(item.FuelType) ? first.FuelTypeId : null);
            var name = string.IsNullOrWhiteSpace(item.FuelType) ? first.FuelType : item.FuelType;
            var index = rows.FindIndex(row => Same(row.FuelTypeId, row.FuelType, id, name));
            if (index < 0)
                rows.Add(new FuelBalance(id, name, 0m, item.Liters));
            else
                rows[index] = rows[index] with { Delivered = rows[index].Delivered + item.Liters };
        }

        return rows;
    }

    // Vyloženo musí po každém palivu přesně sedět na naloženo. Zakázka bez vykládek se nekontroluje.
    public static string? CheckBalance(DispatchOrder order)
    {
        if (order.Stops is not { Count: > 0 })
            return null;
        var wrong = Balance(order).Where(row => row.Loaded != row.Delivered).ToArray();
        if (wrong.Length == 0)
            return null;
        var parts = wrong.Select(row =>
        {
            var difference = row.Delivered - row.Loaded;
            var tail = difference > 0
                ? $"vyloženo o {difference.ToString("N0", Czech)} l víc"
                : $"vyloženo o {(-difference).ToString("N0", Czech)} l méně";
            return $"{row.FuelType}: naloženo {row.Loaded.ToString("N0", Czech)} l, vyloženo {row.Delivered.ToString("N0", Czech)} l ({tail})";
        });
        return $"Vyložené litry nesedí na naložené. {string.Join("; ", parts)}.";
    }

    public static bool SameLoads(DispatchOrder left, DispatchOrder right)
    {
        var a = Effective(left);
        var b = Effective(right);
        if (a.Count != b.Count)
            return false;
        for (var index = 0; index < a.Count; index++)
        {
            if (a[index].FuelTypeId != b[index].FuelTypeId
                || a[index].FuelType != b[index].FuelType
                || a[index].Liters != b[index].Liters
                || CompartmentsText(a[index].Compartments) != CompartmentsText(b[index].Compartments))
                return false;
        }

        return true;
    }

    private static bool Same(int? leftId, string leftName, int? rightId, string rightName)
    {
        if (leftId is not null && rightId is not null)
            return leftId == rightId;
        return string.Equals(leftName.Trim(), rightName.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record OrderLegInput(
    decimal? Kilometers,
    decimal? SalePrice,
    bool? SaleOverridden,
    string? KmSource = null,
    decimal? BilledKilometers = null);

public sealed record OrderRouteSnapshot(
    string PricingMode,
    decimal SalePerKm,
    decimal SalePerLiter,
    decimal CostPerKm,
    IReadOnlyList<OrderStop> Stops,
    IReadOnlyList<OrderLeg> Legs,
    IReadOnlyList<OrderLoad>? Loads = null);

public static class OrderCatalogs
{
    public static OrderCatalog From(DispatchSnapshot snapshot) =>
        From(snapshot, snapshot.DefaultCostPerKm);

    public static OrderCatalog From(DispatchSnapshot snapshot, decimal defaultCostPerKm)
    {
        var rates = snapshot.Vehicles
            .Where(vehicle => vehicle.CostPerKm is not null)
            .ToDictionary(vehicle => vehicle.Id, vehicle => vehicle.CostPerKm!.Value, StringComparer.Ordinal);
        return new OrderCatalog(
            snapshot.Vehicles.Select(vehicle => vehicle.Id).ToHashSet(StringComparer.Ordinal),
            snapshot.Drivers.Select(driver => driver.Id).ToHashSet(StringComparer.Ordinal),
            defaultCostPerKm,
            rates,
            VehicleCompartments: snapshot.Vehicles.ToDictionary(
                vehicle => vehicle.Id,
                vehicle => vehicle.Compartments ?? [],
                StringComparer.Ordinal));
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
            order.Legs ?? [],
            order.Loads is { Count: > 0 } ? order.Loads : null);
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
            Legs = snapshot.Legs,
            Loads = snapshot.Loads is { Count: > 0 } ? snapshot.Loads : order.Loads
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
            PricingMode = mode,
            SalePerKm = salePerKm.Value,
            SalePerLiter = salePerLiter.Value,
            CostPerKm = costPerKm.Value,
            Stops = snapshot.Stops,
            Legs = snapshot.Legs
        };
        return RouteBuild.Ok(order with { Quote = OrderPricing.Summarize(order) }, distanceWrites);
    }

    public static DispatchOrder Display(DispatchOrder order, OrderCatalog catalog)
    {
        return order.Quote is null ? order with { Quote = OrderPricing.Summarize(order) } : order;
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
        if (built.VehicleId is not null
            && catalog.VehicleCostPerKm is not null
            && catalog.VehicleCostPerKm.TryGetValue(built.VehicleId, out var vehicleRate))
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
            if (!string.IsNullOrWhiteSpace(input.Note) && input.Note.Trim().Length > 500)
                return (null, "Poznámka vykládky může mít nejvýše 500 znaků.", null);

            List<OrderStopItem>? items = null;
            (int? Id, string? Name, string? Error) fuel;
            decimal liters;
            if (input.Items is { Count: > 0 })
            {
                var parsed = StopItems(input.Items, index + 1, headerFuelId, headerFuel, fuelTypes);
                if (parsed.Error is not null)
                    return (null, parsed.Error, null);
                items = parsed.Items!;
                fuel = (items[0].FuelTypeId, items[0].FuelType, null);
                liters = OrderPricing.Liters(items.Sum(item => item.Liters));
            }
            else
            {
                if (input.Liters is null)
                    return (null, "Vyplňte litry na vykládce.", null);
                if (input.Liters is < 0m or > 1_000_000m)
                    return (null, "Litry na vykládce musí být od 0 do 1 000 000.", null);
                fuel = StopFuel(input.FuelTypeId, headerFuelId, headerFuel, fuelTypes);
                if (fuel.Error is not null)
                    return (null, fuel.Error, null);
                liters = OrderPricing.Liters(input.Liters.Value);
            }

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
            decimal? billed = null;
            if (legInput?.BilledKilometers is not null)
            {
                if (legInput.BilledKilometers is < 0m or > 100_000m)
                    return (null, "Fakturované km úseku musí být od 0 do 100 000.", null);
                billed = OrderPricing.Kilometers(legInput.BilledKilometers.Value);
            }

            if (decision.Upsert && pair is not null && decision.Kilometers is not null)
                writes.Add(new DistanceWrite(pair.Value.From, pair.Value.To, decision.Kilometers.Value, billed));
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
                fuel.Name,
                items));
            builtLegs.Add(new OrderLeg(
                NewId(),
                sequence,
                from,
                place,
                liters,
                kilometers,
                OrderPricing.Sale(billed ?? kilometers, liters, overridden, overridePrice, mode, salePerKm, salePerLiter),
                overridden,
                OrderPricing.Cost(kilometers, costPerKm),
                decision.Source,
                billed));
        }

        return (new OrderRouteSnapshot(mode, salePerKm, salePerLiter, costPerKm, builtStops, builtLegs), null, writes);
    }

    private static (List<OrderStopItem>? Items, string? Error) StopItems(
        IReadOnlyList<OrderStopItemInput> inputs,
        int sequence,
        int? headerFuelId,
        string headerFuel,
        IReadOnlyList<FuelTypeItem>? fuelTypes)
    {
        if (inputs.Count > OrderStopItems.MaxCount)
            return (null, $"Vykládka {sequence} může mít nejvýše {OrderStopItems.MaxCount} řádků paliv.");
        var items = new List<OrderStopItem>(inputs.Count);
        foreach (var input in inputs)
        {
            var fuel = StopFuel(input.FuelTypeId, headerFuelId, headerFuel, fuelTypes);
            if (fuel.Error is not null)
                return (null, $"Vykládka {sequence}: vyberte palivo.");
            var name = fuel.Name ?? headerFuel;
            if (input.Liters is null)
                return (null, $"Vykládka {sequence}: vyplňte litry ({name}).");
            if (input.Liters is < 0m or > 1_000_000m)
                return (null, $"Vykládka {sequence}: litry musí být od 0 do 1 000 000.");
            IReadOnlyList<int> requested = input.Compartments is { Count: > 0 } ? input.Compartments
                : input.Compartment is { } single ? [single]
                : [];
            if (requested.Any(number => number is < 1 or > VehicleCompartments.MaxCount))
                return (null, $"Vykládka {sequence}: přepážky se číslují 1 až {VehicleCompartments.MaxCount}.");
            var numbers = requested.Distinct().Order().ToArray();
            var clash = items.FirstOrDefault(item => item.FuelTypeId == fuel.Id
                && (numbers.Length == 0
                    ? OrderStopItems.Numbers(item).Count == 0
                    : OrderStopItems.Numbers(item).Intersect(numbers).Any()));
            if (clash is not null)
                return (null, numbers.Length == 0
                    ? $"Vykládka {sequence}: {name} je tam dvakrát. Litry sečtěte do jednoho řádku."
                    : $"Vykládka {sequence}: přepážka {OrderStopItems.Numbers(clash).Intersect(numbers).First()} s palivem {name} je tam dvakrát.");
            items.Add(new OrderStopItem(
                fuel.Id,
                name,
                numbers.Length > 0 ? numbers[0] : null,
                OrderPricing.Liters(input.Liters.Value),
                numbers.Length > 0 ? numbers : null));
        }

        return (items, null);
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
