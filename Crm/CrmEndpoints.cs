using System.Globalization;

namespace PHMTRANS.Crm;

public static class CrmEndpoints
{
    private static readonly StringComparer Czech = CreateCzechComparer();

    public static void MapCrm(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim()
                ?? context.Request.Host.Host;
            var path = context.Request.Path.Value ?? "";
            var authenticated = context.User.Identity?.IsAuthenticated == true;
            var crmApi = context.Request.Path.StartsWithSegments("/api/crm");

            if (!authenticated && (crmApi || IsCrmPage(path)))
            {
                if (crmApi)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { message = "Přihlaste se." });
                    return;
                }

                context.Response.Redirect("/login.html");
                return;
            }

            if (IsCrmHost(host) && !authenticated && IsHome(path))
            {
                context.Response.Redirect("/login.html");
                return;
            }

            if (IsCrmHost(host) && authenticated && (IsHome(path) || IsLogin(path)))
                context.Request.Path = "/crm.html";

            await next();
        });

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

    private static Task<IResult> GetCustomers(string? kind, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (!string.IsNullOrWhiteSpace(kind) && !CrmCustomerKinds.IsKnown(kind.Trim()))
                return Results.BadRequest(new { message = CrmCustomerKinds.UnknownMessage });

            var customers = ToBoard(await store.ReadAsync(cancellationToken)).Customers;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                var selected = kind.Trim();
                customers = customers.Where(item => item.Kind == selected).ToArray();
            }

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

    private static Task<IResult> GetContacts(string? customerId, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            var contacts = ToBoard(await store.ReadAsync(cancellationToken)).Contacts;
            if (!string.IsNullOrWhiteSpace(customerId))
            {
                var selected = customerId.Trim();
                contacts = contacts.Where(item => item.CustomerId == selected).ToArray();
            }

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

    private static Task<IResult> GetOpportunities(string? stage, ICrmStore store, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (!string.IsNullOrWhiteSpace(stage) && !CrmStages.IsKnown(stage.Trim()))
                return Results.BadRequest(new { message = CrmStages.UnknownMessage });

            var opportunities = ToBoard(await store.ReadAsync(cancellationToken)).Opportunities;
            if (!string.IsNullOrWhiteSpace(stage))
            {
                var selected = stage.Trim();
                opportunities = opportunities.Where(item => item.Stage == selected).ToArray();
            }

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
        string? customerId,
        string? opportunityId,
        string? kind,
        ICrmStore store,
        CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            if (!string.IsNullOrWhiteSpace(kind) && !CrmActivityKinds.IsKnown(kind.Trim()))
                return Results.BadRequest(new { message = CrmActivityKinds.UnknownMessage });

            var activities = ToBoard(await store.ReadAsync(cancellationToken)).Activities;
            if (!string.IsNullOrWhiteSpace(customerId))
            {
                var selected = customerId.Trim();
                activities = activities.Where(item => item.CustomerId == selected).ToArray();
            }

            if (!string.IsNullOrWhiteSpace(opportunityId))
            {
                var selected = opportunityId.Trim();
                activities = activities.Where(item => item.OpportunityId == selected).ToArray();
            }

            if (!string.IsNullOrWhiteSpace(kind))
            {
                var selected = kind.Trim();
                activities = activities.Where(item => item.Kind == selected).ToArray();
            }

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

    private static bool IsCrmHost(string host)
    {
        var name = host.Split(':')[0];
        return name.Equals("crm.phmtrans.cz", StringComparison.OrdinalIgnoreCase)
            || name.Equals("www.crm.phmtrans.cz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHome(string path) =>
        path is "" or "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

    private static bool IsLogin(string path) =>
        path.Equals("/login.html", StringComparison.OrdinalIgnoreCase);

    private static bool IsCrmPage(string path) =>
        path.Equals("/crm.html", StringComparison.OrdinalIgnoreCase);

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
