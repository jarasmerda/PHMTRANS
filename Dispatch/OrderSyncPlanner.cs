namespace PHMTRANS.Dispatch;

public enum OrderSyncAction
{
    None,
    Create,
    Update,
    Correction,
    Delete,
    Cancel
}

public sealed record OrderSyncPlan(
    OrderSyncAction Action,
    int? VersionFrom,
    int VersionTo,
    int CorrectionIndex = 1,
    bool IncludeDelete = true)
{
    public static OrderSyncPlan None(int version) => new(OrderSyncAction.None, null, version);

    public string? ActionName => Action switch
    {
        OrderSyncAction.Create => OrderSyncActions.Create,
        OrderSyncAction.Update => OrderSyncActions.Update,
        OrderSyncAction.Correction => OrderSyncActions.Correction,
        OrderSyncAction.Delete => OrderSyncActions.Delete,
        OrderSyncAction.Cancel => OrderSyncActions.Cancel,
        _ => null
    };
}

public static class OrderSyncPlanner
{
    public static OrderSyncPlan Plan(
        DispatchOrder current,
        DispatchOrder? previous,
        DispatchOrder? lastSent,
        bool confirmRequested)
    {
        var hasDocument = current.PohodaOrderId is > 0;
        var differsFromSent = lastSent is not null && !OrderPohodaFields.Same(lastSent, current);

        // Storno se počítá z poslední odeslané verze, ne z předchozí lokální.
        if (current.IsLocked && lastSent is not null && differsFromSent)
            return new OrderSyncPlan(OrderSyncAction.Correction, lastSent.CurrentVersion, current.CurrentVersion);

        if (!current.IsLocked && hasDocument && differsFromSent)
            return new OrderSyncPlan(OrderSyncAction.Update, lastSent!.CurrentVersion, current.CurrentVersion);

        if (!current.IsLocked && hasDocument && lastSent is null
            && (previous is null || !OrderPohodaFields.Same(previous, current)))
            return new OrderSyncPlan(OrderSyncAction.Update, previous?.CurrentVersion, current.CurrentVersion);

        if (current.IsLocked && hasDocument && lastSent is null
            && (previous is null || !OrderPohodaFields.Same(previous, current)))
            return new OrderSyncPlan(OrderSyncAction.Update, previous?.CurrentVersion, current.CurrentVersion);

        // Nová zakázka jde do fronty hned při založení a znovu, když se změní údaje pro Pohodu.
        // Stav „K fakturaci“ už není podmínka odeslání.
        if (!hasDocument)
        {
            var fieldsChanged = previous is null || !OrderPohodaFields.Same(previous, current);
            if (fieldsChanged || confirmRequested)
                return new OrderSyncPlan(OrderSyncAction.Create, null, current.CurrentVersion);
        }

        return OrderSyncPlan.None(current.CurrentVersion);
    }

    public static OrderSyncPlan Force(DispatchOrder current, DispatchOrder? lastSent)
    {
        if (current.PohodaOrderId is null && lastSent is null)
            return new OrderSyncPlan(OrderSyncAction.Create, null, current.CurrentVersion);

        if (current.IsLocked && lastSent is not null && !OrderPohodaFields.Same(lastSent, current))
            return new OrderSyncPlan(OrderSyncAction.Correction, lastSent.CurrentVersion, current.CurrentVersion);

        if (current.PohodaOrderId is > 0 && (lastSent is null || !OrderPohodaFields.Same(lastSent, current)))
            return new OrderSyncPlan(OrderSyncAction.Update, lastSent?.CurrentVersion, current.CurrentVersion);

        return OrderSyncPlan.None(current.CurrentVersion);
    }

    public static bool WasSent(DispatchOrder order) =>
        order.PohodaOrderId is > 0 || order.LastSentVersion is not null;
}

public static class OrderSyncMode
{
    public static string Normalize(string? mode)
    {
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            return "Off";
        if (string.Equals(mode, "DryRun", StringComparison.OrdinalIgnoreCase))
            return "DryRun";
        return "Live";
    }

    public static string QueueStatus(string? mode) =>
        Normalize(mode) == "DryRun" ? OrderQueueStatuses.DryRun : OrderQueueStatuses.Pending;

    public static string Describe(string? mode) => Normalize(mode) switch
    {
        "Off" => "Zápis do Pohody je vypnutý (Off). Fronta se neodesílá.",
        "DryRun" => "Zkouška (DryRun): XML se ukládá do fronty a do Pohody se neposílá.",
        _ => "Zápis do Pohody je zapnutý (Live)."
    };
}
