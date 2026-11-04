using PHMTRANS.Pohoda;

namespace PHMTRANS.Dispatch;

public sealed record OrderCatalog(
    IReadOnlySet<string> VehicleIds,
    IReadOnlySet<string> DriverIds,
    IReadOnlySet<string> TripIds,
    IReadOnlyDictionary<string, TripLink>? Trips = null,
    decimal DefaultCostPerKm = 0m,
    IReadOnlyDictionary<string, decimal>? VehicleCostPerKm = null)
{
    public static OrderCatalog Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

public sealed record QueueUpsert(
    long? TargetId,
    int? VersionFrom,
    int VersionTo,
    string Action,
    string PayloadXml,
    string Status,
    bool ResetAttempts,
    string? Warning = null);

public sealed class OrderEditContext
{
    public required OrderCatalog Catalog { get; init; }

    public required string SyncMode { get; init; }

    public required PohodaOptions Pohoda { get; init; }

    public required DateTimeOffset Now { get; init; }

    public required IReadOnlyCollection<string> ExistingCodes { get; init; }

    public DispatchOrder? Current { get; init; }

    public DispatchOrder? LastSent { get; init; }

    public int SentCorrections { get; init; }
}

public abstract record OrderChange
{
    private OrderChange()
    {
    }

    public sealed record Saved(DispatchOrder Order, bool VersionAdded, QueueUpsert? Queue, bool DropOpenQueue = false) : OrderChange;

    public sealed record Deleted(string Id) : OrderChange;

    public sealed record Rejected(string Message) : OrderChange;
}

public static class OrderWorkflow
{
    public static OrderChange Create(OrderSaveRequest request, OrderEditContext context)
    {
        if (context.Current is not null)
            return new OrderChange.Rejected("Nová zakázka už má id.");

        var draft = Read(request, current: null);
        if (draft.Error is not null)
            return new OrderChange.Rejected(draft.Error);

        var assignment = CheckLinks(draft, context.Catalog);
        if (assignment is not null)
            return new OrderChange.Rejected(assignment);

        var order = Build(draft, context, id: NewId(), code: NextCode(context.ExistingCodes, context.Now), version: 1, createdAt: context.Now);
        var routed = OrderRoute.Materialize(order, request, context.Catalog);
        if (routed.Error is not null)
            return new OrderChange.Rejected(routed.Error);
        return Finish(routed.Order!, previous: null, context, versionAdded: true, confirmRequested: false);
    }

    public static OrderChange Save(OrderSaveRequest request, OrderEditContext context)
    {
        if (context.Current is null)
            return new OrderChange.Rejected("Zakázka neexistuje.");
        if (context.Current.Status == OrderStatuses.Stornovano)
            return new OrderChange.Rejected("Stornovaná zakázka je jen ke čtení.");

        var draft = Read(request, context.Current);
        if (draft.Error is not null)
            return new OrderChange.Rejected(draft.Error);

        var assignment = CheckLinks(draft, context.Catalog);
        if (assignment is not null)
            return new OrderChange.Rejected(assignment);

        var order = Build(draft, context, context.Current.Id, context.Current.Code, context.Current.CurrentVersion + 1, context.Current.CreatedAt);
        var routed = OrderRoute.Materialize(order, request, context.Catalog, context.Current);
        if (routed.Error is not null)
            return new OrderChange.Rejected(routed.Error);
        if (OrderPohodaFields.SameDocument(context.Current, routed.Order!))
            return new OrderChange.Saved(context.Current, false, null);

        return Finish(routed.Order!, context.Current, context, versionAdded: true, confirmRequested: false);
    }

    public static OrderChange Confirm(OrderEditContext context)
    {
        if (context.Current is null)
            return new OrderChange.Rejected("Zakázka neexistuje.");
        if (context.Current.Status == OrderStatuses.Stornovano)
            return new OrderChange.Rejected("Stornovaná zakázka je jen ke čtení.");
        if (context.Current.IsLocked)
            return new OrderChange.Rejected("Zakázka je uzamčená, protože je vyfakturovaná.");

        var changed = context.Current.Status != OrderStatuses.KFakturaci;
        var order = changed
            ? context.Current with
            {
                Status = OrderStatuses.KFakturaci,
                CurrentVersion = context.Current.CurrentVersion + 1,
                UpdatedAt = context.Now
            }
            : context.Current;
        return Finish(order, context.Current, context, changed, confirmRequested: true);
    }

    public static OrderChange MarkInvoiced(string? invoiceNumber, OrderEditContext context)
    {
        if (context.Current is null)
            return new OrderChange.Rejected("Zakázka neexistuje.");
        if (context.Current.Status == OrderStatuses.Stornovano)
            return new OrderChange.Rejected("Stornovaná zakázka je jen ke čtení.");

        var invoiceError = CheckLength(invoiceNumber, 40, "Číslo faktury může mít nejvýše 40 znaků.");
        if (invoiceError is not null)
            return new OrderChange.Rejected(invoiceError);

        var invoice = string.IsNullOrWhiteSpace(invoiceNumber)
            ? context.Current.PohodaInvoiceNumber
            : invoiceNumber.Trim();
        var order = context.Current with
        {
            Status = OrderStatuses.Vyfakturovano,
            IsLocked = true,
            PohodaInvoiceNumber = invoice,
            CurrentVersion = context.Current.CurrentVersion + 1,
            UpdatedAt = context.Now
        };
        if (OrderPohodaFields.SameDocument(context.Current, order))
            return new OrderChange.Saved(context.Current, false, null);

        return Finish(order, context.Current, context, versionAdded: true, confirmRequested: false);
    }

    public static OrderChange Resend(OrderSyncEntry? latest, OrderEditContext context)
    {
        if (context.Current is null)
            return new OrderChange.Rejected("Zakázka neexistuje.");
        if (context.Current.Status == OrderStatuses.Stornovano)
            return new OrderChange.Rejected("Stornovaná zakázka je jen ke čtení.");
        if (!OrderStatuses.IsConfirmed(context.Current.Status))
            return new OrderChange.Rejected("Nejdřív zakázku potvrďte k fakturaci.");

        if (latest is not null && latest.Status is OrderQueueStatuses.Failed or OrderQueueStatuses.DryRun or OrderQueueStatuses.Pending)
        {
            return new OrderChange.Saved(
                context.Current,
                false,
                new QueueUpsert(
                    latest.Id,
                    latest.VersionFrom,
                    latest.VersionTo,
                    latest.Action,
                    latest.PayloadXml,
                    OrderSyncMode.QueueStatus(context.SyncMode),
                    ResetAttempts: true,
                    latest.Warning));
        }

        var plan = WithCorrectionIndex(OrderSyncPlanner.Force(context.Current, context.LastSent), context);
        if (plan.ActionName is null)
            return new OrderChange.Rejected("Není co odeslat znovu.");

        return new OrderChange.Saved(context.Current, false, Queue(context, context.Current, plan, targetId: null));
    }

    public static OrderChange Cancel(string? reason, bool delete, OrderEditContext context)
    {
        if (context.Current is null)
            return new OrderChange.Rejected("Zakázka neexistuje.");
        if (context.Current.Status == OrderStatuses.Stornovano)
            return new OrderChange.Rejected("Zakázka už je stornovaná.");

        var reasonError = CheckLength(reason, 500, "Důvod storna může mít nejvýše 500 znaků.");
        if (reasonError is not null)
            return new OrderChange.Rejected(reasonError);

        var sent = OrderSyncPlanner.WasSent(context.Current);
        if (delete)
        {
            if (sent)
                return new OrderChange.Rejected("Zakázku odeslanou do Pohody nelze smazat, jen stornovat.");
            return new OrderChange.Deleted(context.Current.Id);
        }

        var order = context.Current with
        {
            Status = OrderStatuses.Stornovano,
            TripId = null,
            CancelReason = Blank(reason),
            CurrentVersion = context.Current.CurrentVersion + 1,
            UpdatedAt = context.Now
        };

        if (!sent)
            return new OrderChange.Saved(order, true, null, DropOpenQueue: true);

        if (context.Current.IsLocked)
        {
            if (context.LastSent is null)
                return new OrderChange.Rejected("Chybí poslední odeslaná verze, opravné storno nelze sestavit.");

            var plan = WithCorrectionIndex(
                new OrderSyncPlan(OrderSyncAction.Cancel, context.LastSent.CurrentVersion, order.CurrentVersion),
                context);
            return new OrderChange.Saved(order, true, Queue(context, order, plan, targetId: null));
        }

        if (context.Current.PohodaOrderId is not int id || id < 1)
            return new OrderChange.Rejected("Chybí id objednávky v Pohodě, storno nelze smazat v Pohodě.");

        var deletePlan = new OrderSyncPlan(
            OrderSyncAction.Delete,
            context.LastSent?.CurrentVersion,
            order.CurrentVersion,
            IncludeDelete: true);
        return new OrderChange.Saved(order, true, Queue(context, order, deletePlan, targetId: null));
    }

    public static OrderChange AssignTrip(string? tripId, bool onlyIfFree, OrderEditContext context)
    {
        if (context.Current is null)
            return new OrderChange.Rejected("Zakázka neexistuje.");
        if (context.Current.Status == OrderStatuses.Stornovano)
            return new OrderChange.Rejected("Stornovaná zakázka je jen ke čtení.");

        var trip = Blank(tripId);
        var error = OrderAssignment.Check(context.Current, trip, onlyIfFree, context.Catalog);
        if (error is not null)
            return new OrderChange.Rejected(error);

        var crew = OrderRoute.Crew(trip, context.Current.VehicleId, context.Current.DriverId, context.Catalog);
        var order = context.Current with
        {
            TripId = trip,
            VehicleId = crew.VehicleId,
            DriverId = crew.DriverId,
            UpdatedAt = context.Now
        };
        if (OrderPohodaFields.SameDocument(context.Current, order))
            return new OrderChange.Saved(context.Current, false, null);

        // Jízda, vozidlo a řidič nejsou v Pohodě. Bez nové verze a bez fronty.
        return new OrderChange.Saved(order, false, null);
    }

    private static OrderChange Finish(
        DispatchOrder order,
        DispatchOrder? previous,
        OrderEditContext context,
        bool versionAdded,
        bool confirmRequested)
    {
        var plan = WithCorrectionIndex(OrderSyncPlanner.Plan(order, previous, context.LastSent, confirmRequested), context);
        var queue = plan.ActionName is null ? null : Queue(context, order, plan, targetId: null);
        if (!versionAdded && queue is null)
            return new OrderChange.Saved(previous ?? order, false, null);
        return new OrderChange.Saved(order, versionAdded, queue);
    }

    private static OrderSyncPlan WithCorrectionIndex(OrderSyncPlan plan, OrderEditContext context) =>
        plan.Action is OrderSyncAction.Correction or OrderSyncAction.Cancel
            ? plan with { CorrectionIndex = context.SentCorrections + 1 }
            : plan;

    private static QueueUpsert Queue(OrderEditContext context, DispatchOrder order, OrderSyncPlan plan, long? targetId)
    {
        var xml = OrderPohodaXml.Build(context.Pohoda, plan, order, context.LastSent);
        var warning = plan.Action is OrderSyncAction.Correction or OrderSyncAction.Cancel
            ? OrderPohodaXml.LockedOrderWarning(OrderPohodaXml.CorrectionNumber(order.Code, plan.CorrectionIndex))
            : null;
        return new QueueUpsert(
            targetId,
            plan.VersionFrom,
            plan.VersionTo,
            plan.ActionName!,
            xml,
            OrderSyncMode.QueueStatus(context.SyncMode),
            ResetAttempts: true,
            warning);
    }

    private static DispatchOrder Build(Draft draft, OrderEditContext context, string id, string code, int version, DateTimeOffset createdAt)
    {
        var current = context.Current;
        var (status, locked, invoice) = ResolveState(current, draft.Status, draft.Invoice);
        return new DispatchOrder(
            id,
            code,
            draft.CustomerPohodaId,
            draft.CustomerName!,
            draft.LoadingPlace!,
            draft.UnloadingPlace!,
            draft.LoadingDate!.Value,
            draft.UnloadingDate!.Value,
            draft.FuelType!,
            draft.Liters!.Value,
            draft.AgreedPrice!.Value,
            draft.Surcharges!.Value,
            draft.Note,
            draft.CmrNumber,
            draft.VehicleId,
            draft.DriverId,
            draft.TripId,
            status,
            locked,
            version,
            current?.PohodaOrderId,
            current?.PohodaOrderNumber,
            invoice,
            current?.LastSentVersion,
            createdAt,
            context.Now,
            current?.LatestSyncStatus,
            current?.LatestSyncError,
            current?.LatestSyncWarning,
            CancelReason: current?.CancelReason,
            CustomerIco: draft.CustomerIco,
            CustomerDic: draft.CustomerDic,
            CustomerStreet: draft.CustomerStreet,
            CustomerCity: draft.CustomerCity,
            CustomerZip: draft.CustomerZip,
            CustomerEmail: draft.CustomerEmail,
            CustomerPhone: draft.CustomerPhone);
    }

    private static (string Status, bool Locked, string? Invoice) ResolveState(
        DispatchOrder? current,
        string? requestedStatus,
        string? requestedInvoice)
    {
        if (current?.IsLocked == true)
        {
            var kept = string.IsNullOrWhiteSpace(requestedInvoice) ? current.PohodaInvoiceNumber : requestedInvoice.Trim();
            return (OrderStatuses.Vyfakturovano, true, kept);
        }

        var status = string.IsNullOrWhiteSpace(requestedStatus)
            ? current?.Status ?? OrderStatuses.Nova
            : requestedStatus.Trim();
        var invoice = string.IsNullOrWhiteSpace(requestedInvoice) ? null : requestedInvoice.Trim();
        var locked = status == OrderStatuses.Vyfakturovano || invoice is not null;
        if (locked)
            status = OrderStatuses.Vyfakturovano;
        return (status, locked, invoice);
    }

    private static Draft Read(OrderSaveRequest request, DispatchOrder? current)
    {
        if (request.CustomerPohodaId is 0 or < 0)
            return Draft.Fail("Id zákazníka v Pohodě musí být kladné číslo.");
        var customerId = request.CustomerPohodaId ?? current?.CustomerPohodaId;
        if (current is null && customerId is null)
            return Draft.Fail("Zákazníka vyberte z adresáře Pohody.");
        var keepCustomer = current is not null && customerId == current.CustomerPohodaId;
        var ico = Snapshot(request.CustomerIco, current?.CustomerIco, keepCustomer, 15, "IČO může mít nejvýše 15 znaků.");
        if (ico.Error is not null)
            return Draft.Fail(ico.Error);
        var dic = Snapshot(request.CustomerDic, current?.CustomerDic, keepCustomer, 18, "DIČ může mít nejvýše 18 znaků.");
        if (dic.Error is not null)
            return Draft.Fail(dic.Error);
        var street = Snapshot(request.CustomerStreet, current?.CustomerStreet, keepCustomer, 64, "Ulice může mít nejvýše 64 znaků.");
        if (street.Error is not null)
            return Draft.Fail(street.Error);
        var city = Snapshot(request.CustomerCity, current?.CustomerCity, keepCustomer, 45, "Obec může mít nejvýše 45 znaků.");
        if (city.Error is not null)
            return Draft.Fail(city.Error);
        var zip = Snapshot(request.CustomerZip, current?.CustomerZip, keepCustomer, 15, "PSČ může mít nejvýše 15 znaků.");
        if (zip.Error is not null)
            return Draft.Fail(zip.Error);
        var email = Snapshot(request.CustomerEmail, current?.CustomerEmail, keepCustomer, 98, "E-mail může mít nejvýše 98 znaků.");
        if (email.Error is not null)
            return Draft.Fail(email.Error);
        var phone = Snapshot(request.CustomerPhone, current?.CustomerPhone, keepCustomer, 40, "Telefon může mít nejvýše 40 znaků.");
        if (phone.Error is not null)
            return Draft.Fail(phone.Error);

        var customer = Required(request.CustomerName, 160, "Vyplňte zákazníka.", "Zákazník může mít nejvýše 160 znaků.");
        if (customer.Error is not null)
            return Draft.Fail(customer.Error);
        var loading = Required(request.LoadingPlace, 160, "Vyplňte místo nakládky.", "Místo nakládky může mít nejvýše 160 znaků.");
        if (loading.Error is not null)
            return Draft.Fail(loading.Error);
        var unloadingSource = request.UnloadingPlace;
        if (string.IsNullOrWhiteSpace(unloadingSource) && request.Stops is { Count: > 0 })
            unloadingSource = request.Stops[^1].Place;
        var unloading = Required(unloadingSource, 160, "Vyplňte místo vykládky.", "Místo vykládky může mít nejvýše 160 znaků.");
        if (unloading.Error is not null)
            return Draft.Fail(unloading.Error);
        var fuel = Required(request.FuelType, 80, "Vyplňte druh paliva.", "Druh paliva může mít nejvýše 80 znaků.");
        if (fuel.Error is not null)
            return Draft.Fail(fuel.Error);

        if (request.LoadingDate is null)
            return Draft.Fail("Vyplňte datum nakládky.");
        if (request.UnloadingDate is null)
            return Draft.Fail("Vyplňte datum vykládky.");
        if (request.UnloadingDate.Value < request.LoadingDate.Value)
            return Draft.Fail("Vykládka nemůže být před nakládkou.");
        if (request.Liters is null)
            return Draft.Fail("Vyplňte litry.");
        if (request.Liters < 0 || request.Liters > 1_000_000m)
            return Draft.Fail("Litry musí být od 0 do 1 000 000.");
        var agreedPrice = request.AgreedPrice ?? 0m;
        if (request.Stops is null && request.AgreedPrice is null)
            return Draft.Fail("Vyplňte dohodnutou cenu.");
        if (agreedPrice < 0 || agreedPrice > 1_000_000_000m)
            return Draft.Fail("Dohodnutá cena musí být od 0 do 1 000 000 000.");
        var surcharges = request.Surcharges ?? 0m;
        if (surcharges is < -1_000_000_000m or > 1_000_000_000m)
            return Draft.Fail("Příplatky jsou mimo povolený rozsah.");

        var noteError = CheckLength(request.Note, 500, "Poznámka může mít nejvýše 500 znaků.");
        if (noteError is not null)
            return Draft.Fail(noteError);
        var cmrError = CheckLength(request.CmrNumber, 40, "Číslo CMR může mít nejvýše 40 znaků.");
        if (cmrError is not null)
            return Draft.Fail(cmrError);
        var invoiceError = CheckLength(request.PohodaInvoiceNumber, 40, "Číslo faktury může mít nejvýše 40 znaků.");
        if (invoiceError is not null)
            return Draft.Fail(invoiceError);

        var status = string.IsNullOrWhiteSpace(request.Status) ? current?.Status ?? OrderStatuses.Nova : request.Status.Trim();
        if (!OrderStatuses.IsKnown(status))
            return Draft.Fail(OrderStatuses.UnknownMessage);
        if (status == OrderStatuses.Stornovano)
            return Draft.Fail("Storno zakázky se dělá tlačítkem Stornovat.");

        return new Draft(
            null,
            customerId,
            customer.Value,
            loading.Value,
            unloading.Value,
            request.LoadingDate,
            request.UnloadingDate,
            fuel.Value,
            decimal.Round(request.Liters.Value, 3, MidpointRounding.AwayFromZero),
            OrderPohodaFields.Money(agreedPrice),
            OrderPohodaFields.Money(surcharges),
            Blank(request.Note),
            Blank(request.CmrNumber),
            Blank(request.VehicleId),
            Blank(request.DriverId),
            Blank(request.TripId),
            status,
            Blank(request.PohodaInvoiceNumber),
            ico.Value,
            dic.Value,
            street.Value,
            city.Value,
            zip.Value,
            email.Value,
            phone.Value);
    }

    private static string? CheckLinks(Draft draft, OrderCatalog catalog)
    {
        if (draft.TripId is null && draft.VehicleId is not null && !catalog.VehicleIds.Contains(draft.VehicleId))
            return "Neznámé vozidlo.";
        if (draft.TripId is null && draft.DriverId is not null && !catalog.DriverIds.Contains(draft.DriverId))
            return "Neznámý řidič.";
        if (draft.TripId is not null && !catalog.TripIds.Contains(draft.TripId))
            return "Neznámá jízda.";
        return null;
    }

    private static (string? Value, string? Error) Required(string? value, int max, string missing, string tooLong)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, missing);
        var trimmed = value.Trim();
        return trimmed.Length > max ? (null, tooLong) : (trimmed, null);
    }

    private static string? CheckLength(string? value, int max, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().Length > max ? message : null;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (string? Value, string? Error) Snapshot(string? requested, string? current, bool keep, int max, string tooLong)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return (keep ? current : null, null);
        var trimmed = requested.Trim();
        return trimmed.Length > max ? (null, tooLong) : (trimmed, null);
    }

    private static string NewId() => Guid.NewGuid().ToString("n");

    public static string NextCode(IReadOnlyCollection<string> codes, DateTimeOffset now)
    {
        var prefix = $"Z{now.Year}-";
        var highest = 0;
        foreach (var code in codes)
        {
            if (!code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(code.AsSpan(prefix.Length), out var number))
                highest = Math.Max(highest, number);
        }

        return $"{prefix}{(highest + 1):0000}";
    }

    private sealed record Draft(
        string? Error,
        int? CustomerPohodaId,
        string? CustomerName,
        string? LoadingPlace,
        string? UnloadingPlace,
        DateOnly? LoadingDate,
        DateOnly? UnloadingDate,
        string? FuelType,
        decimal? Liters,
        decimal? AgreedPrice,
        decimal? Surcharges,
        string? Note,
        string? CmrNumber,
        string? VehicleId,
        string? DriverId,
        string? TripId,
        string? Status,
        string? Invoice,
        string? CustomerIco = null,
        string? CustomerDic = null,
        string? CustomerStreet = null,
        string? CustomerCity = null,
        string? CustomerZip = null,
        string? CustomerEmail = null,
        string? CustomerPhone = null)
    {
        public static Draft Fail(string error) => new(error, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
    }
}
