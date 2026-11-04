using System.Globalization;
using PHMTRANS;

namespace PHMTRANS.Crm;

public static class CrmEndpoints
{
    private static readonly StringComparer Czech = CreateCzechComparer();

    public static void MapCrm(this WebApplication app)
    {
        // Stejně jako /api/dispatch. RequireAuthorization() by skončilo prázdným 401
        // ještě před tímto middleware, protože UseAuthorization je registrované dřív.
        var api = app.MapGroup("/api/crm");
        api.MapGet("/board", GetBoard);
        api.MapGet("/customers", GetCustomers);
        api.MapGet("/customers/{id}", GetCustomer);
        api.MapPost("/customers", CreateCustomer);
        api.MapPut("/customers/{id}", UpdateCustomer);
        api.MapPatch("/customers/{id}", UpdateCustomer);
        api.MapGet("/contacts", GetContacts);
        api.MapPost("/contacts", CreateContact);
        api.MapPut("/contacts/{id}", UpdateContact);
        api.MapPatch("/contacts/{id}", UpdateContact);
        api.MapGet("/opportunities", GetOpportunities);
        api.MapPost("/opportunities", CreateOpportunity);
        api.MapPut("/opportunities/{id}", UpdateOpportunity);
        api.MapPatch("/opportunities/{id}", UpdateOpportunity);
        api.MapGet("/activities", GetActivities);
        api.MapPost("/activities", CreateActivity);
        api.MapPut("/activities/{id}", UpdateActivity);
        api.MapPatch("/activities/{id}", UpdateActivity);
    }

    private static Task<IResult> GetBoard(ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () => Results.Ok(ToBoard(await store.ReadAsync(cancellationToken))));

    private static Task<IResult> GetCustomers(string[]? kind, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (Reject(kind, CrmCustomerKinds.All, CrmCustomerKinds.UnknownMessage, out var selected) is { } error)
                return error;

            var customers = ToBoard(await store.ReadAsync(cancellationToken)).Customers;
            if (selected.Count > 0)
                customers = customers.Where(item => selected.Contains(item.Kind)).ToArray();

            return Results.Ok(customers);
        });

    private static Task<IResult> GetCustomer(string id, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var board = ToBoard(await store.ReadAsync(cancellationToken));
            var customer = board.Customers.FirstOrDefault(item => item.Id == id);
            if (customer is null)
                return Results.NotFound(new { message = CrmTexts.CustomerMissing });

            var contacts = board.Contacts.Where(item => item.CustomerId == id).ToArray();
            return Results.Ok(new CrmCustomerDetail(customer, contacts));
        });

    private static Task<IResult> CreateCustomer(NewCustomerRequest request, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.AddCustomerAsync(request, cancellationToken)));

    private static Task<IResult> UpdateCustomer(
        string id,
        UpdateCustomerRequest request,
        ICrmStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.UpdateCustomerAsync(id, request, cancellationToken)));

    private static Task<IResult> GetContacts(string[]? customerId, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (Reject(customerId, null, "", out var selected) is { } error)
                return error;

            var contacts = ToBoard(await store.ReadAsync(cancellationToken)).Contacts;
            if (selected.Count > 0)
                contacts = contacts.Where(item => selected.Contains(item.CustomerId)).ToArray();

            return Results.Ok(contacts);
        });

    private static Task<IResult> CreateContact(NewContactRequest request, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.AddContactAsync(request, cancellationToken)));

    private static Task<IResult> UpdateContact(
        string id,
        UpdateContactRequest request,
        ICrmStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.UpdateContactAsync(id, request, cancellationToken)));

    private static Task<IResult> GetOpportunities(string[]? stage, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (Reject(stage, CrmStages.All, CrmStages.UnknownMessage, out var selected) is { } error)
                return error;

            var opportunities = ToBoard(await store.ReadAsync(cancellationToken)).Opportunities;
            if (selected.Count > 0)
                opportunities = opportunities.Where(item => selected.Contains(item.Stage)).ToArray();

            return Results.Ok(opportunities);
        });

    private static Task<IResult> CreateOpportunity(NewOpportunityRequest request, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.AddOpportunityAsync(request, cancellationToken)));

    private static Task<IResult> UpdateOpportunity(
        string id,
        UpdateOpportunityRequest request,
        ICrmStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.UpdateOpportunityAsync(id, request, cancellationToken)));

    private static Task<IResult> GetActivities(
        string[]? customerId,
        string? opportunityId,
        string[]? kind,
        ICrmStore store,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (Reject(kind, CrmActivityKinds.All, CrmActivityKinds.UnknownMessage, out var kinds) is { } kindError)
                return kindError;
            if (Reject(customerId, null, "", out var customers) is { } customerError)
                return customerError;

            var activities = ToBoard(await store.ReadAsync(cancellationToken)).Activities;
            if (customers.Count > 0)
                activities = activities.Where(item => customers.Contains(item.CustomerId)).ToArray();

            if (!string.IsNullOrWhiteSpace(opportunityId))
            {
                var selected = opportunityId.Trim();
                activities = activities.Where(item => item.OpportunityId == selected).ToArray();
            }

            if (kinds.Count > 0)
                activities = activities.Where(item => kinds.Contains(item.Kind)).ToArray();

            return Results.Ok(activities);
        });

    private static Task<IResult> CreateActivity(NewActivityRequest request, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.AddActivityAsync(request, cancellationToken)));

    private static Task<IResult> UpdateActivity(
        string id,
        UpdateActivityRequest request,
        ICrmStore store,
        CancellationToken cancellationToken) =>
        Execute(async () => ToResult(await store.UpdateActivityAsync(id, request, cancellationToken)));

    private static IResult? Reject(
        IEnumerable<string?>? raw,
        IReadOnlyList<string>? known,
        string unknownMessage,
        out IReadOnlyList<string> values)
    {
        var selected = ValueSelection.Parse(raw, known, unknownMessage);
        if (selected.Error is not null)
        {
            values = [];
            return Results.BadRequest(new { message = selected.Error });
        }

        values = selected.ExplicitAll ? [] : selected.Values;
        return null;
    }

    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (CrmStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult ToResult(CrmMutation mutation) => mutation switch
    {
        CrmMutation.CustomerSaved saved => Results.Ok(saved.Customer),
        CrmMutation.ContactSaved saved => Results.Ok(saved.Contact),
        CrmMutation.OpportunitySaved saved => Results.Ok(saved.Opportunity),
        CrmMutation.ActivitySaved saved => Results.Ok(saved.Activity),
        CrmMutation.NotFound missing => Results.NotFound(new { message = missing.Message }),
        CrmMutation.Rejected rejected => Results.BadRequest(new { message = rejected.Message }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };

    private static CrmBoard ToBoard(CrmSnapshot snapshot)
    {
        var customers = snapshot.Customers
            .OrderBy(item => item.Name, Czech)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        var contacts = snapshot.Contacts
            .OrderBy(item => item.Name, Czech)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var opportunities = snapshot.Opportunities
            .OrderBy(item => CrmStages.Rank(item.Stage))
            .ThenBy(item => item.Title, Czech)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        var activities = snapshot.Activities
            .OrderByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        var counts = new CrmCounts(
            customers.Length,
            opportunities.Length,
            activities.Length,
            CrmStages.All.Select(stage => new CrmStageCount(stage, opportunities.Count(item => item.Stage == stage))).ToArray(),
            CrmActivityKinds.All.Select(kind => new CrmKindCount(kind, activities.Count(item => item.Kind == kind))).ToArray());
        return new CrmBoard(CrmStorage.Kind, CrmStorage.Note, counts, customers, contacts, opportunities, activities);
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
