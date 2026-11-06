using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHMTRANS.Crm;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class CrmHttpTests : IClassFixture<CrmApp>
{
    private readonly CrmApp _app;

    public CrmHttpTests(CrmApp app) => _app = app;

    [Fact]
    public async Task Crm_host_opens_the_board_and_other_hosts_stay_on_their_pages()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var board = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/board", user: false, host: "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Unauthorized, board.StatusCode);
        Assert.Contains("Přihlaste se", await board.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var crmHome = await client.SendAsync(Request(HttpMethod.Get, "/", user: false, host: "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.Redirect, crmHome.StatusCode);
        Assert.Equal("/login.html", crmHome.Headers.Location?.OriginalString);

        var crmPage = await client.SendAsync(Request(HttpMethod.Get, "/crm.html", user: false));
        Assert.Equal(HttpStatusCode.Redirect, crmPage.StatusCode);
        Assert.StartsWith("/login.html", crmPage.Headers.Location?.OriginalString, StringComparison.Ordinal);

        var portal = await client.SendAsync(Request(HttpMethod.Get, "/", user: false));
        var portalHtml = await portal.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        Assert.Contains("https://intranet.phmtrans.cz/\">Intranet</a>", portalHtml, StringComparison.Ordinal);

        var signedCrm = await client.SendAsync(Request(HttpMethod.Get, "/CRM", user: true, host: "intranet.phmtrans.cz"));
        Assert.Equal(HttpStatusCode.OK, signedCrm.StatusCode);
        var crmHtml = await signedCrm.Content.ReadAsStringAsync();
        Assert.Contains("<h1>CRM</h1>", crmHtml, StringComparison.Ordinal);
        Assert.Contains("crm.js?v=2", crmHtml, StringComparison.Ordinal);
        Assert.Contains("multi-filter.js?v=1", crmHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Dispečink</h1>", crmHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Vydané faktury", crmHtml, StringComparison.Ordinal);

        var signedErp = await client.SendAsync(Request(HttpMethod.Get, "/ERP", user: true, host: "intranet.phmtrans.cz"));
        var erpHtml = await signedErp.Content.ReadAsStringAsync();
        Assert.Contains("Vydané faktury", erpHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>CRM</h1>", erpHtml, StringComparison.Ordinal);

        var signedDispatch = await client.SendAsync(Request(HttpMethod.Get, "/Dispecink", user: true, host: "intranet.phmtrans.cz"));
        var dispatchHtml = await signedDispatch.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Dispečink</h1>", dispatchHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>CRM</h1>", dispatchHtml, StringComparison.Ordinal);

        var data = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/board"));
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        using var json = JsonDocument.Parse(await data.Content.ReadAsStringAsync());
        Assert.Equal("sql", json.RootElement.GetProperty("storage").GetString());
        Assert.Contains("Pohody", json.RootElement.GetProperty("storageNote").GetString(), StringComparison.Ordinal);
        Assert.Contains("Crm_*", json.RootElement.GetProperty("storageNote").GetString(), StringComparison.Ordinal);
        Assert.Equal(5, json.RootElement.GetProperty("counts").GetProperty("stages").GetArrayLength());
        Assert.Equal(3, json.RootElement.GetProperty("counts").GetProperty("activityKinds").GetArrayLength());
        Assert.Contains(json.RootElement.GetProperty("customers").EnumerateArray(), item => item.GetProperty("name").GetString() == "Velkoobchod Demo s.r.o.");
    }

    [Fact]
    public async Task Customer_contact_opportunity_and_activity_can_be_created_and_edited()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var denied = await client.SendAsync(Request(HttpMethod.Put, "/api/crm/customers/cust-1", """{"kind":"Firma","name":"A"}""", user: false));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var created = await client.SendAsync(Request(HttpMethod.Post, "/api/crm/customers", """
            {
              "kind": " Firma ",
              "name": "  Zákazník HTTP  ",
              "ico": "87654321",
              "email": "http@demo.cz",
              "phone": "+420 700 000 001",
              "street": "Průmyslová 4",
              "city": "Pardubice",
              "zip": "530 02",
              "country": "Česká republika",
              "note": "z webu"
            }
            """));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var customerId = createdJson.RootElement.GetProperty("id").GetString();
        var customerCode = createdJson.RootElement.GetProperty("code").GetString();
        Assert.Equal("Zákazník HTTP", createdJson.RootElement.GetProperty("name").GetString());
        Assert.StartsWith("C", customerCode, StringComparison.Ordinal);

        var renamed = await client.SendAsync(Request(HttpMethod.Put, $"/api/crm/customers/{customerId}", """
            {
              "kind": "Osoba",
              "name": "Zákazník HTTP po úpravě",
              "email": "jinak@demo.cz",
              "city": "Chrudim"
            }
            """));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using var renamedJson = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync());
        Assert.Equal(customerCode, renamedJson.RootElement.GetProperty("code").GetString());
        Assert.Equal("Osoba", renamedJson.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Chrudim", renamedJson.RootElement.GetProperty("city").GetString());
        Assert.Equal(JsonValueKind.Null, renamedJson.RootElement.GetProperty("ico").ValueKind);

        var contact = await client.SendAsync(Request(HttpMethod.Post, "/api/crm/contacts", $$"""
            { "customerId": "{{customerId}}", "name": "  HTTP kontakt  ", "role": "nákup", "phone": "+420 700 000 002" }
            """));
        Assert.Equal(HttpStatusCode.OK, contact.StatusCode);
        using var contactJson = JsonDocument.Parse(await contact.Content.ReadAsStringAsync());
        var contactId = contactJson.RootElement.GetProperty("id").GetString();
        Assert.Equal("HTTP kontakt", contactJson.RootElement.GetProperty("name").GetString());

        var contactEdit = await client.SendAsync(Request(HttpMethod.Put, $"/api/crm/contacts/{contactId}", $$"""
            { "customerId": "{{customerId}}", "name": "HTTP kontakt po úpravě", "role": "obchod", "email": "kontakt@demo.cz" }
            """));
        Assert.Equal(HttpStatusCode.OK, contactEdit.StatusCode);
        using var contactEditJson = JsonDocument.Parse(await contactEdit.Content.ReadAsStringAsync());
        Assert.Equal("obchod", contactEditJson.RootElement.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, contactEditJson.RootElement.GetProperty("phone").ValueKind);

        var detail = await client.SendAsync(Request(HttpMethod.Get, $"/api/crm/customers/{customerId}"));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal("Zákazník HTTP po úpravě", detailJson.RootElement.GetProperty("customer").GetProperty("name").GetString());
        Assert.Contains(detailJson.RootElement.GetProperty("contacts").EnumerateArray(), item => item.GetProperty("id").GetString() == contactId);

        var opportunity = await client.SendAsync(Request(HttpMethod.Post, "/api/crm/opportunities", $$"""
            {
              "customerId": "{{customerId}}",
              "title": "Obchod HTTP",
              "amount": 1500.5,
              "expectedClose": "2026-12-01",
              "note": "nabídka"
            }
            """));
        Assert.Equal(HttpStatusCode.OK, opportunity.StatusCode);
        using var opportunityJson = JsonDocument.Parse(await opportunity.Content.ReadAsStringAsync());
        var opportunityId = opportunityJson.RootElement.GetProperty("id").GetString();
        var opportunityCode = opportunityJson.RootElement.GetProperty("code").GetString();
        Assert.Equal(CrmStages.Novy, opportunityJson.RootElement.GetProperty("stage").GetString());
        Assert.Equal(1500.5m, opportunityJson.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-12-01", opportunityJson.RootElement.GetProperty("expectedClose").GetString());

        var won = await client.SendAsync(Request(HttpMethod.Put, $"/api/crm/opportunities/{opportunityId}", $$"""
            {
              "customerId": "{{customerId}}",
              "title": "Obchod HTTP vyhrán",
              "stage": "Vyhráno",
              "amount": 1800,
              "expectedClose": "2026-12-15"
            }
            """));
        Assert.Equal(HttpStatusCode.OK, won.StatusCode);
        using var wonJson = JsonDocument.Parse(await won.Content.ReadAsStringAsync());
        Assert.Equal(opportunityCode, wonJson.RootElement.GetProperty("code").GetString());
        Assert.Equal(CrmStages.Vyhrano, wonJson.RootElement.GetProperty("stage").GetString());
        Assert.Equal(1800m, wonJson.RootElement.GetProperty("amount").GetDecimal());

        var activity = await client.SendAsync(Request(HttpMethod.Post, "/api/crm/activities", $$"""
            {
              "kind": "Hovor",
              "opportunityId": "{{opportunityId}}",
              "subject": "Hovor HTTP",
              "body": "Domluveno",
              "occurredAt": "2026-11-02T06:00:00Z"
            }
            """));
        Assert.Equal(HttpStatusCode.OK, activity.StatusCode);
        using var activityJson = JsonDocument.Parse(await activity.Content.ReadAsStringAsync());
        var activityId = activityJson.RootElement.GetProperty("id").GetString();
        Assert.Equal(customerId, activityJson.RootElement.GetProperty("customerId").GetString());
        Assert.Equal(opportunityId, activityJson.RootElement.GetProperty("opportunityId").GetString());
        Assert.StartsWith("A", activityJson.RootElement.GetProperty("code").GetString(), StringComparison.Ordinal);

        var activityEdit = await client.SendAsync(Request(HttpMethod.Put, $"/api/crm/activities/{activityId}", $$"""
            {
              "kind": "Schůzka",
              "customerId": "{{customerId}}",
              "opportunityId": "{{opportunityId}}",
              "subject": "Schůzka HTTP",
              "occurredAt": "2026-11-03T09:30:00Z"
            }
            """));
        Assert.Equal(HttpStatusCode.OK, activityEdit.StatusCode);
        using var activityEditJson = JsonDocument.Parse(await activityEdit.Content.ReadAsStringAsync());
        Assert.Equal(CrmActivityKinds.Schuzka, activityEditJson.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Schůzka HTTP", activityEditJson.RootElement.GetProperty("subject").GetString());
        Assert.Equal(JsonValueKind.Null, activityEditJson.RootElement.GetProperty("body").ValueKind);

        var board = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/board"));
        using var boardJson = JsonDocument.Parse(await board.Content.ReadAsStringAsync());
        Assert.Contains(boardJson.RootElement.GetProperty("customers").EnumerateArray(), item => item.GetProperty("id").GetString() == customerId && item.GetProperty("name").GetString() == "Zákazník HTTP po úpravě");
        Assert.Contains(boardJson.RootElement.GetProperty("opportunities").EnumerateArray(), item => item.GetProperty("id").GetString() == opportunityId && item.GetProperty("stage").GetString() == CrmStages.Vyhrano);
        Assert.Contains(boardJson.RootElement.GetProperty("activities").EnumerateArray(), item => item.GetProperty("id").GetString() == activityId && item.GetProperty("kind").GetString() == CrmActivityKinds.Schuzka);

        var names = boardJson.RootElement.GetProperty("customers").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToList();
        Assert.True(names.IndexOf("Petra Malá") < names.IndexOf("Velkoobchod Demo s.r.o."));
    }

    [Fact]
    public async Task Filters_reject_unknown_values_and_missing_rows_are_not_found()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var firms = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/customers?kind=Firma"));
        Assert.Equal(HttpStatusCode.OK, firms.StatusCode);
        using var firmsJson = JsonDocument.Parse(await firms.Content.ReadAsStringAsync());
        Assert.Contains(firmsJson.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == "cust-1");
        Assert.All(firmsJson.RootElement.EnumerateArray(), item => Assert.Equal(CrmCustomerKinds.Firma, item.GetProperty("kind").GetString()));

        var unknownKind = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/customers?kind=Družstvo"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
        Assert.Contains(CrmCustomerKinds.UnknownMessage, await unknownKind.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var won = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/opportunities?stage=Vyhráno"));
        Assert.Equal(HttpStatusCode.OK, won.StatusCode);
        using var wonJson = JsonDocument.Parse(await won.Content.ReadAsStringAsync());
        Assert.Contains(wonJson.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == "opp-4");
        Assert.All(wonJson.RootElement.EnumerateArray(), item => Assert.Equal(CrmStages.Vyhrano, item.GetProperty("stage").GetString()));

        var badStage = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/opportunities?stage=Hotovo"));
        Assert.Equal(HttpStatusCode.BadRequest, badStage.StatusCode);

        var calls = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/activities?kind=Hovor&customerId=cust-1"));
        Assert.Equal(HttpStatusCode.OK, calls.StatusCode);
        using var callsJson = JsonDocument.Parse(await calls.Content.ReadAsStringAsync());
        Assert.Contains(callsJson.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == "act-1");
        Assert.All(callsJson.RootElement.EnumerateArray(), item => Assert.Equal(CrmActivityKinds.Hovor, item.GetProperty("kind").GetString()));

        var bothKinds = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/customers?kind=Firma&kind=Osoba"));
        Assert.Equal(HttpStatusCode.OK, bothKinds.StatusCode);
        using var bothKindsJson = JsonDocument.Parse(await bothKinds.Content.ReadAsStringAsync());
        var kinds = bothKindsJson.RootElement.EnumerateArray().Select(item => item.GetProperty("kind").GetString()).ToArray();
        Assert.Contains(CrmCustomerKinds.Firma, kinds);
        Assert.Contains(CrmCustomerKinds.Osoba, kinds);

        var allStages = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/opportunities?stage=*"));
        Assert.Equal(HttpStatusCode.OK, allStages.StatusCode);
        using var allStagesJson = JsonDocument.Parse(await allStages.Content.ReadAsStringAsync());
        Assert.Contains(allStagesJson.RootElement.EnumerateArray(), item => item.GetProperty("stage").GetString() == CrmStages.Prohrano);
        Assert.Contains(allStagesJson.RootElement.EnumerateArray(), item => item.GetProperty("stage").GetString() == CrmStages.Vyhrano);

        var twoStages = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/opportunities?stage=Nový&stage=Vyhráno"));
        using var twoStagesJson = JsonDocument.Parse(await twoStages.Content.ReadAsStringAsync());
        Assert.All(twoStagesJson.RootElement.EnumerateArray(), item =>
            Assert.Contains(item.GetProperty("stage").GetString(), new[] { CrmStages.Novy, CrmStages.Vyhrano }));

        var twoActivities = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/activities?kind=Hovor&kind=Poznámka"));
        using var twoActivitiesJson = JsonDocument.Parse(await twoActivities.Content.ReadAsStringAsync());
        Assert.All(twoActivitiesJson.RootElement.EnumerateArray(), item =>
            Assert.Contains(item.GetProperty("kind").GetString(), new[] { CrmActivityKinds.Hovor, CrmActivityKinds.Poznamka }));

        var contacts = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/contacts?customerId=cust-1&customerId=cust-2"));
        Assert.Equal(HttpStatusCode.OK, contacts.StatusCode);
        using var contactsJson = JsonDocument.Parse(await contacts.Content.ReadAsStringAsync());
        Assert.Contains(contactsJson.RootElement.EnumerateArray(), item => item.GetProperty("name").GetString() == "Jan Novák");
        Assert.All(contactsJson.RootElement.EnumerateArray(), item => Assert.Equal("cust-1", item.GetProperty("customerId").GetString()));

        var missingCustomer = await client.SendAsync(Request(HttpMethod.Get, "/api/crm/customers/cust-missing"));
        Assert.Equal(HttpStatusCode.NotFound, missingCustomer.StatusCode);
        Assert.Contains(CrmTexts.CustomerMissing, await missingCustomer.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var missingOpportunity = await client.SendAsync(Request(HttpMethod.Put, "/api/crm/opportunities/opp-missing", """
            { "customerId": "cust-1", "title": "Nikdo", "stage": "Nový" }
            """));
        Assert.Equal(HttpStatusCode.NotFound, missingOpportunity.StatusCode);
        Assert.Contains(CrmTexts.OpportunityMissing, await missingOpportunity.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var rejected = await client.SendAsync(Request(HttpMethod.Post, "/api/crm/activities", """
            { "kind": "Hovor", "subject": "Nikam", "occurredAt": "2026-11-02T06:00:00Z" }
            """));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("u zákazníka nebo u příležitosti", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var negative = await client.SendAsync(Request(HttpMethod.Post, "/api/crm/opportunities", """
            { "customerId": "cust-1", "title": "Mínus", "amount": -5 }
            """));
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Contains("Částka nesmí být záporná", await negative.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? json = null, bool user = true, string? host = null)
    {
        var message = new HttpRequestMessage(method, path);
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (host is not null)
            message.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        if (user)
            message.Headers.TryAddWithoutValidation("X-Test-User", "obchod");
        return message;
    }
}

public sealed class CrmApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindContentRoot());
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, CrmTestAuthHandler>("Test", _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            });
            foreach (var descriptor in services.Where(item => item.ServiceType == typeof(ICrmStore)).ToList())
                services.Remove(descriptor);
            services.AddSingleton<ICrmStore, MemoryCrmStore>();
        });
    }

    private static string FindContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "wwwroot", "crm.html")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

public sealed class CrmWithoutDatabaseTests : IClassFixture<CrmBareApp>
{
    private readonly CrmBareApp _app;

    public CrmWithoutDatabaseTests(CrmBareApp app) => _app = app;

    [Fact]
    public async Task Health_stays_up_and_crm_explains_the_missing_connection_string()
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var anonymous = await client.GetAsync("/api/crm/board");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var board = new HttpRequestMessage(HttpMethod.Get, "/api/crm/board");
        board.Headers.TryAddWithoutValidation("X-Test-User", "obchod");
        var response = await client.SendAsync(board);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("ConnectionStrings:Dispatch", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var erp = await client.GetAsync("/erp.html");
        Assert.Equal(HttpStatusCode.Redirect, erp.StatusCode);
    }
}

public sealed class CrmBareApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindContentRoot());
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, CrmTestAuthHandler>("Test", _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            });
        });
    }

    private static string FindContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "wwwroot", "crm.html")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

file sealed class CrmTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-User"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = TestUsers.Identity(Request, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
