using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PHMTRANS.Crm;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class CrmStoreTests
{
    private readonly MemoryCrmStore _store = new();

    [Fact]
    public async Task Seed_covers_every_stage_kind_and_customer_type()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Customers.Count);
        Assert.Equal(CrmCustomerKinds.All.Count, snapshot.Customers.Select(item => item.Kind).Distinct().Count());
        Assert.Equal(2, snapshot.Contacts.Count(item => item.CustomerId == "cust-1"));
        Assert.DoesNotContain(snapshot.Contacts, item => item.CustomerId == "cust-2");
        Assert.Equal(CrmStages.All.Count, snapshot.Opportunities.Select(item => item.Stage).Distinct().Count());
        Assert.Equal(CrmActivityKinds.All.Count, snapshot.Activities.Select(item => item.Kind).Distinct().Count());
    }

    [Fact]
    public async Task New_customer_gets_the_next_code_and_rejects_an_empty_name()
    {
        var rejected = await _store.AddCustomerAsync(
            new NewCustomerRequest(CrmCustomerKinds.Firma, "  ", null, null, null, null, null, null, null, null),
            CancellationToken.None);
        Assert.Equal("Vyplňte název.", Assert.IsType<CrmMutation.Rejected>(rejected).Message);

        var created = await _store.AddCustomerAsync(
            new NewCustomerRequest("  Firma  ", "  Zákazník Test  ", " 12345678 ", " info@test.cz ", " 601 ", " Ulice 1 ", " Praha ", " 110 00 ", " Česká republika ", "  poznámka "),
            CancellationToken.None);
        var customer = Assert.IsType<CrmMutation.CustomerSaved>(created).Customer;
        var year = DateTime.Now.Year;
        Assert.Equal(year == 2026 ? "C2026-0003" : $"C{year}-0001", customer.Code);
        Assert.Equal(CrmCustomerKinds.Firma, customer.Kind);
        Assert.Equal("Zákazník Test", customer.Name);
        Assert.Equal("12345678", customer.Ico);
        Assert.Equal("info@test.cz", customer.Email);
        Assert.Equal("poznámka", customer.Note);

        var again = await _store.ReadAsync(CancellationToken.None);
        Assert.Contains(again.Customers, item => item.Id == customer.Id && item.Name == "Zákazník Test");
    }

    [Fact]
    public async Task Customer_codes_stay_independent_and_unknown_kind_is_rejected()
    {
        var before = await _store.ReadAsync(CancellationToken.None);
        var unknown = await _store.AddCustomerAsync(
            new NewCustomerRequest("Družstvo", "Zákazník", null, null, null, null, null, null, null, null),
            CancellationToken.None);
        Assert.Equal(CrmCustomerKinds.UnknownMessage, Assert.IsType<CrmMutation.Rejected>(unknown).Message);

        var person = await _store.AddCustomerAsync(
            new NewCustomerRequest(CrmCustomerKinds.Osoba, "Karel Dvořák", null, null, null, null, null, null, null, null),
            CancellationToken.None);
        var deal = await _store.AddOpportunityAsync(
            new NewOpportunityRequest(Assert.IsType<CrmMutation.CustomerSaved>(person).Customer.Id, "Nový obchod", null, null, null, null),
            CancellationToken.None);
        var activity = await _store.AddActivityAsync(
            new NewActivityRequest(CrmActivityKinds.Hovor, Assert.IsType<CrmMutation.CustomerSaved>(person).Customer.Id, null, "Hovor", null, DateTimeOffset.Now),
            CancellationToken.None);

        var year = DateTime.Now.Year;
        Assert.Equal(year == 2026 ? "C2026-0003" : $"C{year}-0001", Assert.IsType<CrmMutation.CustomerSaved>(person).Customer.Code);
        Assert.Equal(year == 2026 ? "O2026-0006" : $"O{year}-0001", Assert.IsType<CrmMutation.OpportunitySaved>(deal).Opportunity.Code);
        Assert.Equal(year == 2026 ? "A2026-0004" : $"A{year}-0001", Assert.IsType<CrmMutation.ActivitySaved>(activity).Activity.Code);
        Assert.Equal(CrmStages.Novy, Assert.IsType<CrmMutation.OpportunitySaved>(deal).Opportunity.Stage);

        var after = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(before.Customers.Count + 1, after.Customers.Count);
        Assert.DoesNotContain(after.Customers, item => item.Name == "Zákazník" && item.Kind == "Družstvo");
    }

    [Fact]
    public async Task Update_saves_customer_fields_and_keeps_the_code()
    {
        var result = await _store.UpdateCustomerAsync("cust-1", new UpdateCustomerRequest(
            CrmCustomerKinds.Osoba,
            "  Velkoobchod po úpravě  ",
            "",
            "nove@demo.cz",
            "",
            "Nová 8",
            "Litomyšl",
            "570 01",
            "",
            "upraveno"), CancellationToken.None);
        var updated = Assert.IsType<CrmMutation.CustomerSaved>(result).Customer;

        Assert.Equal("C2026-0001", updated.Code);
        Assert.Equal(CrmCustomerKinds.Osoba, updated.Kind);
        Assert.Equal("Velkoobchod po úpravě", updated.Name);
        Assert.Null(updated.Ico);
        Assert.Equal("nove@demo.cz", updated.Email);
        Assert.Null(updated.Phone);
        Assert.Equal("Nová 8", updated.Street);
        Assert.Null(updated.Country);
        Assert.Equal("upraveno", updated.Note);

        var again = await _store.ReadAsync(CancellationToken.None);
        var stored = again.Customers.Single(item => item.Id == "cust-1");
        Assert.Equal("Velkoobchod po úpravě", stored.Name);
        Assert.Equal("Petra Malá", again.Customers.Single(item => item.Id == "cust-2").Name);
        Assert.Equal(2, again.Contacts.Count(item => item.CustomerId == "cust-1"));
    }

    [Fact]
    public async Task Update_rejects_unknown_customer_and_a_too_long_name()
    {
        var before = await _store.ReadAsync(CancellationToken.None);
        var missing = await _store.UpdateCustomerAsync("cust-missing", Copy(before.Customers[0]), CancellationToken.None);
        Assert.Equal(CrmTexts.CustomerMissing, Assert.IsType<CrmMutation.NotFound>(missing).Message);

        var tooLong = await _store.UpdateCustomerAsync("cust-2", Copy(before.Customers[1]) with { Name = new string('A', 161) }, CancellationToken.None);
        Assert.Equal("Název může mít nejvýše 160 znaků.", Assert.IsType<CrmMutation.Rejected>(tooLong).Message);

        var after = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(before.Customers.Single(item => item.Id == "cust-2").Name, after.Customers.Single(item => item.Id == "cust-2").Name);
        Assert.Equal(before.Customers.Count, after.Customers.Count);
    }

    [Fact]
    public async Task Contact_can_be_created_and_moved_to_another_customer()
    {
        var created = await _store.AddContactAsync(
            new NewContactRequest(" cust-2 ", "  Alena Malá  ", " jednatelka ", " alena@example.cz ", "", " "),
            CancellationToken.None);
        var contact = Assert.IsType<CrmMutation.ContactSaved>(created).Contact;
        Assert.Equal("cust-2", contact.CustomerId);
        Assert.Equal("Alena Malá", contact.Name);
        Assert.Equal("jednatelka", contact.Role);
        Assert.Null(contact.Phone);
        Assert.Null(contact.Note);

        var moved = await _store.UpdateContactAsync(contact.Id, new UpdateContactRequest(
            "cust-1", "Alena Malá", "obchod", "alena@demo.cz", "+420 605 000 005", "převedeno"), CancellationToken.None);
        var updated = Assert.IsType<CrmMutation.ContactSaved>(moved).Contact;
        Assert.Equal("cust-1", updated.CustomerId);
        Assert.Equal("převedeno", updated.Note);

        var unknown = await _store.AddContactAsync(
            new NewContactRequest("cust-missing", "Nikdo", null, null, null, null),
            CancellationToken.None);
        Assert.Equal(CrmTexts.UnknownCustomer, Assert.IsType<CrmMutation.Rejected>(unknown).Message);

        var missing = await _store.UpdateContactAsync("con-missing", new UpdateContactRequest("cust-1", "Nikdo", null, null, null, null), CancellationToken.None);
        Assert.Equal(CrmTexts.ContactMissing, Assert.IsType<CrmMutation.NotFound>(missing).Message);

        var again = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal("cust-1", again.Contacts.Single(item => item.Id == contact.Id).CustomerId);
        Assert.Equal(3, again.Contacts.Count);
    }

    [Fact]
    public async Task Opportunity_defaults_to_new_and_edit_keeps_an_explicit_stage()
    {
        var created = await _store.AddOpportunityAsync(
            new NewOpportunityRequest("cust-2", "  Osobní odběr  ", "  ", 10.126m, new DateOnly(2026, 12, 1), null),
            CancellationToken.None);
        var opportunity = Assert.IsType<CrmMutation.OpportunitySaved>(created).Opportunity;
        var year = DateTime.Now.Year;
        Assert.Equal(year == 2026 ? "O2026-0006" : $"O{year}-0001", opportunity.Code);
        Assert.Equal(CrmStages.Novy, opportunity.Stage);
        Assert.Equal(10.13m, opportunity.Amount);
        Assert.Equal(new DateOnly(2026, 12, 1), opportunity.ExpectedClose);

        var won = await _store.UpdateOpportunityAsync(opportunity.Id, new UpdateOpportunityRequest(
            "cust-1", "Osobní odběr po úpravě", CrmStages.Vyhrano, null, null, "hotovo"), CancellationToken.None);
        var updated = Assert.IsType<CrmMutation.OpportunitySaved>(won).Opportunity;
        Assert.Equal(opportunity.Code, updated.Code);
        Assert.Equal(CrmStages.Vyhrano, updated.Stage);
        Assert.Equal("cust-1", updated.CustomerId);
        Assert.Null(updated.Amount);
        Assert.Null(updated.ExpectedClose);
        Assert.Equal("hotovo", updated.Note);

        var again = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(CrmStages.Vyhrano, again.Opportunities.Single(item => item.Id == opportunity.Id).Stage);
    }

    [Fact]
    public async Task Opportunity_rejects_unknown_stage_customer_and_negative_amount()
    {
        var before = await _store.ReadAsync(CancellationToken.None);
        var negative = await _store.AddOpportunityAsync(
            new NewOpportunityRequest("cust-1", "Obchod", CrmStages.Novy, -1m, null, null),
            CancellationToken.None);
        Assert.Equal("Částka nesmí být záporná.", Assert.IsType<CrmMutation.Rejected>(negative).Message);

        var unknownStage = await _store.UpdateOpportunityAsync("opp-1", new UpdateOpportunityRequest(
            "cust-1", "Nafta na zimu", "Hotovo", 120000m, null, null), CancellationToken.None);
        Assert.Equal(CrmStages.UnknownMessage, Assert.IsType<CrmMutation.Rejected>(unknownStage).Message);

        var unknownCustomer = await _store.AddOpportunityAsync(
            new NewOpportunityRequest("cust-missing", "Obchod", null, null, null, null),
            CancellationToken.None);
        Assert.Equal(CrmTexts.UnknownCustomer, Assert.IsType<CrmMutation.Rejected>(unknownCustomer).Message);

        var missing = await _store.UpdateOpportunityAsync("opp-missing", new UpdateOpportunityRequest(
            "cust-1", "Obchod", CrmStages.Novy, null, null, null), CancellationToken.None);
        Assert.Equal(CrmTexts.OpportunityMissing, Assert.IsType<CrmMutation.NotFound>(missing).Message);

        var emptyStage = await _store.UpdateOpportunityAsync("opp-1", new UpdateOpportunityRequest(
            "cust-1", "Nafta na zimu", " ", 120000m, null, null), CancellationToken.None);
        Assert.Equal("Vyplňte fázi.", Assert.IsType<CrmMutation.Rejected>(emptyStage).Message);

        var after = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(before.Opportunities.Count, after.Opportunities.Count);
        Assert.Equal(CrmStages.Novy, after.Opportunities.Single(item => item.Id == "opp-1").Stage);
        Assert.Equal(120000m, after.Opportunities.Single(item => item.Id == "opp-1").Amount);
    }

    [Fact]
    public async Task Activity_links_to_customer_or_opportunity_and_copies_the_customer()
    {
        var when = new DateTimeOffset(2026, 11, 2, 8, 30, 0, TimeSpan.FromHours(1));
        var onlyCustomer = await _store.AddActivityAsync(
            new NewActivityRequest(CrmActivityKinds.Poznamka, "cust-2", " ", "  Poznámka k osobě  ", " text ", when),
            CancellationToken.None);
        var note = Assert.IsType<CrmMutation.ActivitySaved>(onlyCustomer).Activity;
        Assert.Equal("cust-2", note.CustomerId);
        Assert.Null(note.OpportunityId);
        Assert.Equal("Poznámka k osobě", note.Subject);
        Assert.Equal(when, note.OccurredAt);

        var onlyDeal = await _store.AddActivityAsync(
            new NewActivityRequest(CrmActivityKinds.Hovor, null, "opp-5", "Hovor k prohře", null, when),
            CancellationToken.None);
        var call = Assert.IsType<CrmMutation.ActivitySaved>(onlyDeal).Activity;
        Assert.Equal("cust-2", call.CustomerId);
        Assert.Equal("opp-5", call.OpportunityId);

        var mismatch = await _store.AddActivityAsync(
            new NewActivityRequest(CrmActivityKinds.Schuzka, "cust-1", "opp-5", "Špatná vazba", null, when),
            CancellationToken.None);
        Assert.Equal("Zákazník aktivity nesedí se zákazníkem příležitosti.", Assert.IsType<CrmMutation.Rejected>(mismatch).Message);

        var neither = await _store.AddActivityAsync(
            new NewActivityRequest(CrmActivityKinds.Hovor, "", null, "Nikam", null, when),
            CancellationToken.None);
        Assert.Equal("Aktivita musí být u zákazníka nebo u příležitosti.", Assert.IsType<CrmMutation.Rejected>(neither).Message);

        var unknownDeal = await _store.AddActivityAsync(
            new NewActivityRequest(CrmActivityKinds.Hovor, null, "opp-missing", "Nikam", null, when),
            CancellationToken.None);
        Assert.Equal(CrmTexts.UnknownOpportunity, Assert.IsType<CrmMutation.Rejected>(unknownDeal).Message);

        var edited = await _store.UpdateActivityAsync(note.Id, new UpdateActivityRequest(
            CrmActivityKinds.Schuzka, "cust-2", "opp-5", "Schůzka po poznámce", "", when.AddHours(1)), CancellationToken.None);
        var updated = Assert.IsType<CrmMutation.ActivitySaved>(edited).Activity;
        Assert.Equal(note.Code, updated.Code);
        Assert.Equal(CrmActivityKinds.Schuzka, updated.Kind);
        Assert.Equal("opp-5", updated.OpportunityId);
        Assert.Null(updated.Body);
        Assert.Equal(when.AddHours(1), updated.OccurredAt);

        var again = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(CrmActivityKinds.Schuzka, again.Activities.Single(item => item.Id == note.Id).Kind);
        Assert.DoesNotContain(again.Activities, item => item.Subject == "Špatná vazba" || item.Subject == "Nikam");
    }

    [Fact]
    public async Task Activity_requires_a_term_and_a_known_kind()
    {
        var before = await _store.ReadAsync(CancellationToken.None);
        var noTerm = await _store.AddActivityAsync(
            new NewActivityRequest(CrmActivityKinds.Hovor, "cust-1", null, "Bez termínu", null, null),
            CancellationToken.None);
        Assert.Equal("Vyplňte termín.", Assert.IsType<CrmMutation.Rejected>(noTerm).Message);

        var unknown = await _store.UpdateActivityAsync("act-1", new UpdateActivityRequest(
            "Mail", "cust-1", null, "Poptávka nafty", "Voláno na nákup", DateTimeOffset.Now), CancellationToken.None);
        Assert.Equal(CrmActivityKinds.UnknownMessage, Assert.IsType<CrmMutation.Rejected>(unknown).Message);

        var missing = await _store.UpdateActivityAsync("act-missing", new UpdateActivityRequest(
            CrmActivityKinds.Hovor, "cust-1", null, "Předmět", null, DateTimeOffset.Now), CancellationToken.None);
        Assert.Equal(CrmTexts.ActivityMissing, Assert.IsType<CrmMutation.NotFound>(missing).Message);

        var after = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(before.Activities.Count, after.Activities.Count);
        Assert.Equal(CrmActivityKinds.Hovor, after.Activities.Single(item => item.Id == "act-1").Kind);
    }

    [Fact]
    public void Json_binds_optional_crm_fields()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var customer = JsonSerializer.Deserialize<NewCustomerRequest>(
            """{"kind":"Osoba","name":"Karel"}""",
            options);
        Assert.Equal("Osoba", customer!.Kind);
        Assert.Null(customer.Ico);
        Assert.Null(customer.Note);

        var opportunity = JsonSerializer.Deserialize<NewOpportunityRequest>(
            """{"customerId":"cust-1","title":"Nafta","stage":"Nabídka","amount":1500.5,"expectedClose":"2026-12-01"}""",
            options);
        Assert.Equal(1500.5m, opportunity!.Amount);
        Assert.Equal(new DateOnly(2026, 12, 1), opportunity.ExpectedClose);
        Assert.Equal("Nabídka", opportunity.Stage);
        Assert.Null(opportunity.Note);

        var activity = JsonSerializer.Deserialize<NewActivityRequest>(
            """{"kind":"Hovor","opportunityId":"opp-1","subject":"Hovor","occurredAt":"2026-11-02T06:00:00Z"}""",
            options);
        Assert.Null(activity!.CustomerId);
        Assert.Equal("opp-1", activity.OpportunityId);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero), activity.OccurredAt);
        Assert.Null(activity.Body);
    }

    private static UpdateCustomerRequest Copy(CrmCustomer customer) =>
        new(customer.Kind, customer.Name, customer.Ico, customer.Email, customer.Phone, customer.Street, customer.City, customer.Zip, customer.Country, customer.Note);
}

public sealed class CrmPageTests
{
    [Fact]
    public void Crm_page_uses_shared_styles_and_links_the_other_modules()
    {
        var root = FindWwwRoot();
        var html = File.ReadAllText(Path.Combine(root, "crm.html"));
        var js = File.ReadAllText(Path.Combine(root, "crm.js"));
        var index = File.ReadAllText(Path.Combine(root, "index.html"));
        var webConfig = File.ReadAllText(Path.Combine(root, "web.config"));

        Assert.Contains("<h1>CRM</h1>", html, StringComparison.Ordinal);
        Assert.Contains("erp.css?v=8", html, StringComparison.Ordinal);
        Assert.Contains("class=\"brand\" href=\"https://phmtrans.cz/\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"site-back\" href=\"https://phmtrans.cz/\">Zpět na web</a>", html, StringComparison.Ordinal);
        Assert.Contains("crm.js?v=1", html, StringComparison.Ordinal);
        Assert.Contains("class=\"module-links\"", html, StringComparison.Ordinal);
        Assert.Contains("https://intranet.phmtrans.cz/ERP", html, StringComparison.Ordinal);
        Assert.Contains("https://intranet.phmtrans.cz/Dispecink", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">CRM</span>", html, StringComparison.Ordinal);
        Assert.Contains(">Odhlásit</button>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Rozcestník", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.phmtrans.cz/", html, StringComparison.Ordinal);
        Assert.Contains(">Přehled</button>", html, StringComparison.Ordinal);
        Assert.Contains(">Zákazníci</button>", html, StringComparison.Ordinal);
        Assert.Contains(">Příležitosti</button>", html, StringComparison.Ordinal);
        Assert.Contains(">Aktivity</button>", html, StringComparison.Ordinal);
        Assert.Contains("Nový zákazník", html, StringComparison.Ordinal);
        Assert.Contains("Nový kontakt", html, StringComparison.Ordinal);
        Assert.Contains("Nová příležitost", html, StringComparison.Ordinal);
        Assert.Contains("Nová aktivita", html, StringComparison.Ordinal);
        Assert.Contains(">Firma</option>", html, StringComparison.Ordinal);
        Assert.Contains(">Osoba</option>", html, StringComparison.Ordinal);
        Assert.Contains(">Vyhráno</option>", html, StringComparison.Ordinal);
        Assert.Contains(">Prohráno</option>", html, StringComparison.Ordinal);
        Assert.Contains(">Hovor</option>", html, StringComparison.Ordinal);
        Assert.Contains(">Schůzka</option>", html, StringComparison.Ordinal);
        Assert.Contains(">Poznámka</option>", html, StringComparison.Ordinal);
        Assert.Contains(">Upravit</button>", js, StringComparison.Ordinal);
        Assert.Contains("Uložit změny", js, StringComparison.Ordinal);
        Assert.Contains("Označit příležitost jako prohranou?", js, StringComparison.Ordinal);
        Assert.Contains("cache: \"no-store\"", js, StringComparison.Ordinal);
        Assert.Contains("/api/crm/board", js, StringComparison.Ordinal);
        Assert.Contains("/api/crm/customers", js, StringComparison.Ordinal);
        Assert.Contains("/api/crm/contacts", js, StringComparison.Ordinal);
        Assert.Contains("/api/crm/opportunities", js, StringComparison.Ordinal);
        Assert.Contains("/api/crm/activities", js, StringComparison.Ordinal);
        Assert.Contains(", \"PUT\")", js, StringComparison.Ordinal);
        Assert.Contains("https://intranet.phmtrans.cz/CRM", index, StringComparison.Ordinal);
        Assert.Contains(">CRM</a>", index, StringComparison.Ordinal);
        Assert.True(index.IndexOf("https://intranet.phmtrans.cz/Dispecink", StringComparison.Ordinal) < index.IndexOf("https://intranet.phmtrans.cz/CRM", StringComparison.Ordinal));
        Assert.Contains("name=\"CrmHome\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"CrmApi\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("crm.html", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"DispHome\"", webConfig, StringComparison.Ordinal);
        Assert.Contains("name=\"ErpHome\"", webConfig, StringComparison.Ordinal);
    }

    private static string FindWwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var wwwroot = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(wwwroot, "crm.html")))
                return wwwroot;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

public sealed class CrmSqlGuardTests
{
    [Fact]
    public void Schema_script_creates_missing_crm_tables_and_does_not_destroy_anything()
    {
        var script = CrmSchema.Script;

        Assert.Contains("dbo.Crm_Customer", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Crm_Contact", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Crm_Opportunity", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Crm_Activity", script, StringComparison.Ordinal);
        Assert.Equal(4, Count(script, "IF OBJECT_ID"));
        Assert.Equal(4, Count(script, "CREATE TABLE"));
        Assert.Contains("CONSTRAINT CK_Crm_Activity_Target CHECK (CustomerId IS NOT NULL OR OpportunityId IS NOT NULL)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Disp_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Smerco_", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pohoda", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Crm_code_uses_the_dispatch_connection_string_and_does_not_call_pohoda()
    {
        var dir = FindCrmDir();
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("PohodaMServer", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE FROM", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP TABLE", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TRUNCATE", source, StringComparison.OrdinalIgnoreCase);
        }

        var store = File.ReadAllText(Path.Combine(dir, "SqlCrmStore.cs"));
        Assert.Contains("GetConnectionString(\"Dispatch\")", store, StringComparison.Ordinal);
        Assert.DoesNotContain("GetConnectionString(\"Crm\")", store, StringComparison.Ordinal);
        Assert.Contains("PHMTRANS_Crm", store, StringComparison.Ordinal);
        Assert.Equal("sql", CrmStorage.Kind);
        Assert.Contains("Crm_*", CrmStorage.Note, StringComparison.Ordinal);
        Assert.Contains("Pohody", CrmStorage.Note, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:Dispatch", CrmStorage.MissingConnectionMessage, StringComparison.Ordinal);

        var settings = File.ReadAllText(Path.Combine(Directory.GetParent(dir)!.FullName, "appsettings.json"));
        Assert.Contains("\"Dispatch\": \"\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", settings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_connection_string_does_not_open_sql_and_explains_the_gap()
    {
        var store = new SqlCrmStore(new ConfigurationBuilder().Build(), NullLogger<SqlCrmStore>.Instance);
        var customer = new NewCustomerRequest(CrmCustomerKinds.Firma, "Zákazník", null, null, null, null, null, null, null, null);

        var read = await Assert.ThrowsAsync<CrmStoreException>(() => store.ReadAsync(CancellationToken.None));
        var created = await Assert.ThrowsAsync<CrmStoreException>(() => store.AddCustomerAsync(customer, CancellationToken.None));
        var updated = await Assert.ThrowsAsync<CrmStoreException>(() => store.UpdateCustomerAsync(
            "cust-1",
            new UpdateCustomerRequest(CrmCustomerKinds.Firma, "Zákazník", null, null, null, null, null, null, null, null),
            CancellationToken.None));

        Assert.Contains("ConnectionStrings:Dispatch", read.Message, StringComparison.Ordinal);
        Assert.Equal(read.Message, created.Message);
        Assert.Equal(read.Message, updated.Message);
        await store.TryInitializeAsync(CancellationToken.None);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static string FindCrmDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var crm = Path.Combine(dir.FullName, "Crm");
            if (File.Exists(Path.Combine(crm, "CrmSchema.cs")))
                return crm;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezena složka CRM.");
    }
}
