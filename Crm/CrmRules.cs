namespace PHMTRANS.Crm;

public static class CrmRules
{
    private const decimal MaxAmount = 9999999999999999.99m;

    public static CrmSnapshot Snapshot(CrmDocument document) =>
        new(
            document.Customers.ToArray(),
            document.Contacts.ToArray(),
            document.Opportunities.ToArray(),
            document.Activities.ToArray());

    public static CrmMutation AddCustomer(CrmDocument document, NewCustomerRequest request, DateTimeOffset now)
    {
        if (!TryReadCustomer(request.Kind, request.Name, request.Ico, request.Email, request.Phone, request.Street, request.City, request.Zip, request.Country, request.Note, out var fields, out var error))
            return new CrmMutation.Rejected(error!);

        var customer = new CrmCustomer(
            NewId(document.Customers.Select(item => item.Id)),
            NextCode(document.Customers.Select(item => item.Code), 'C', now),
            fields.Kind,
            fields.Name,
            fields.Ico,
            fields.Email,
            fields.Phone,
            fields.Street,
            fields.City,
            fields.Zip,
            fields.Country,
            fields.Note,
            now);
        document.Customers.Add(customer);
        return new CrmMutation.CustomerSaved(customer);
    }

    public static CrmMutation UpdateCustomer(CrmDocument document, string id, UpdateCustomerRequest request, DateTimeOffset now)
    {
        var index = IndexOf(document.Customers, item => item.Id == id);
        if (index < 0)
            return new CrmMutation.NotFound(CrmTexts.CustomerMissing);

        if (!TryReadCustomer(request.Kind, request.Name, request.Ico, request.Email, request.Phone, request.Street, request.City, request.Zip, request.Country, request.Note, out var fields, out var error))
            return new CrmMutation.Rejected(error!);

        var updated = document.Customers[index] with
        {
            Kind = fields.Kind,
            Name = fields.Name,
            Ico = fields.Ico,
            Email = fields.Email,
            Phone = fields.Phone,
            Street = fields.Street,
            City = fields.City,
            Zip = fields.Zip,
            Country = fields.Country,
            Note = fields.Note,
            UpdatedAt = now
        };
        document.Customers[index] = updated;
        return new CrmMutation.CustomerSaved(updated);
    }

    public static CrmMutation AddContact(CrmDocument document, NewContactRequest request, DateTimeOffset now)
    {
        if (!TryReadContact(document, request.CustomerId, request.Name, request.Role, request.Email, request.Phone, request.Note, out var fields, out var error))
            return new CrmMutation.Rejected(error!);

        var contact = new CrmContact(
            NewId(document.Contacts.Select(item => item.Id)),
            fields.CustomerId,
            fields.Name,
            fields.Role,
            fields.Email,
            fields.Phone,
            fields.Note,
            now);
        document.Contacts.Add(contact);
        return new CrmMutation.ContactSaved(contact);
    }

    public static CrmMutation UpdateContact(CrmDocument document, string id, UpdateContactRequest request, DateTimeOffset now)
    {
        var index = IndexOf(document.Contacts, item => item.Id == id);
        if (index < 0)
            return new CrmMutation.NotFound(CrmTexts.ContactMissing);

        if (!TryReadContact(document, request.CustomerId, request.Name, request.Role, request.Email, request.Phone, request.Note, out var fields, out var error))
            return new CrmMutation.Rejected(error!);

        var updated = document.Contacts[index] with
        {
            CustomerId = fields.CustomerId,
            Name = fields.Name,
            Role = fields.Role,
            Email = fields.Email,
            Phone = fields.Phone,
            Note = fields.Note,
            UpdatedAt = now
        };
        document.Contacts[index] = updated;
        return new CrmMutation.ContactSaved(updated);
    }

    public static CrmMutation AddOpportunity(CrmDocument document, NewOpportunityRequest request, DateTimeOffset now)
    {
        if (!TryReadOpportunity(document, request.CustomerId, request.Title, request.Stage, request.Amount, request.Note, defaultStage: true, out var fields, out var error))
            return new CrmMutation.Rejected(error!);

        var opportunity = new CrmOpportunity(
            NewId(document.Opportunities.Select(item => item.Id)),
            NextCode(document.Opportunities.Select(item => item.Code), 'O', now),
            fields.CustomerId,
            fields.Title,
            fields.Stage,
            fields.Amount,
            request.ExpectedClose,
            fields.Note,
            now);
        document.Opportunities.Add(opportunity);
        return new CrmMutation.OpportunitySaved(opportunity);
    }

    public static CrmMutation UpdateOpportunity(CrmDocument document, string id, UpdateOpportunityRequest request, DateTimeOffset now)
    {
        var index = IndexOf(document.Opportunities, item => item.Id == id);
        if (index < 0)
            return new CrmMutation.NotFound(CrmTexts.OpportunityMissing);

        if (!TryReadOpportunity(document, request.CustomerId, request.Title, request.Stage, request.Amount, request.Note, defaultStage: false, out var fields, out var error))
            return new CrmMutation.Rejected(error!);

        var updated = document.Opportunities[index] with
        {
            CustomerId = fields.CustomerId,
            Title = fields.Title,
            Stage = fields.Stage,
            Amount = fields.Amount,
            ExpectedClose = request.ExpectedClose,
            Note = fields.Note,
            UpdatedAt = now
        };
        document.Opportunities[index] = updated;
        return new CrmMutation.OpportunitySaved(updated);
    }

    public static CrmMutation AddActivity(CrmDocument document, NewActivityRequest request, DateTimeOffset now)
    {
        if (!TryReadActivity(document, request.Kind, request.CustomerId, request.OpportunityId, request.Subject, request.Body, request.OccurredAt, out var fields, out var error))
            return new CrmMutation.Rejected(error!);

        var activity = new CrmActivity(
            NewId(document.Activities.Select(item => item.Id)),
            NextCode(document.Activities.Select(item => item.Code), 'A', now),
            fields.Kind,
            fields.CustomerId,
            fields.OpportunityId,
            fields.Subject,
            fields.Body,
            fields.OccurredAt,
            now);
        document.Activities.Add(activity);
        return new CrmMutation.ActivitySaved(activity);
    }

    public static CrmMutation UpdateActivity(CrmDocument document, string id, UpdateActivityRequest request, DateTimeOffset now)
    {
        var index = IndexOf(document.Activities, item => item.Id == id);
        if (index < 0)
            return new CrmMutation.NotFound(CrmTexts.ActivityMissing);

        if (!TryReadActivity(document, request.Kind, request.CustomerId, request.OpportunityId, request.Subject, request.Body, request.OccurredAt, out var fields, out var error))
            return new CrmMutation.Rejected(error!);

        var updated = document.Activities[index] with
        {
            Kind = fields.Kind,
            CustomerId = fields.CustomerId,
            OpportunityId = fields.OpportunityId,
            Subject = fields.Subject,
            Body = fields.Body,
            OccurredAt = fields.OccurredAt,
            UpdatedAt = now
        };
        document.Activities[index] = updated;
        return new CrmMutation.ActivitySaved(updated);
    }

    private static bool TryReadCustomer(
        string? kind,
        string? name,
        string? ico,
        string? email,
        string? phone,
        string? street,
        string? city,
        string? zip,
        string? country,
        string? note,
        out CustomerFields fields,
        out string? error)
    {
        fields = default;
        if (!TryReadCustomerKind(kind, out var selectedKind, out error))
            return false;
        if (!TryReadRequired(name, 160, "Vyplňte název.", "Název může mít nejvýše 160 znaků.", out var customerName, out error))
            return false;
        if (!TryReadOptional(ico, 16, "IČO může mít nejvýše 16 znaků.", out var customerIco, out error))
            return false;
        if (!TryReadOptional(email, 160, "E-mail může mít nejvýše 160 znaků.", out var customerEmail, out error))
            return false;
        if (!TryReadOptional(phone, 40, "Telefon může mít nejvýše 40 znaků.", out var customerPhone, out error))
            return false;
        if (!TryReadOptional(street, 160, "Ulice může mít nejvýše 160 znaků.", out var customerStreet, out error))
            return false;
        if (!TryReadOptional(city, 80, "Město může mít nejvýše 80 znaků.", out var customerCity, out error))
            return false;
        if (!TryReadOptional(zip, 16, "PSČ může mít nejvýše 16 znaků.", out var customerZip, out error))
            return false;
        if (!TryReadOptional(country, 80, "Stát může mít nejvýše 80 znaků.", out var customerCountry, out error))
            return false;
        if (!TryReadOptional(note, 500, "Poznámka může mít nejvýše 500 znaků.", out var customerNote, out error))
            return false;

        fields = new CustomerFields(selectedKind, customerName!, customerIco, customerEmail, customerPhone, customerStreet, customerCity, customerZip, customerCountry, customerNote);
        return true;
    }

    private static bool TryReadContact(
        CrmDocument document,
        string? customerId,
        string? name,
        string? role,
        string? email,
        string? phone,
        string? note,
        out ContactFields fields,
        out string? error)
    {
        fields = default;
        if (!TryExistingCustomer(document, customerId, out var customer, out error))
            return false;
        if (!TryReadRequired(name, 160, "Vyplňte jméno kontaktu.", "Jméno může mít nejvýše 160 znaků.", out var contactName, out error))
            return false;
        if (!TryReadOptional(role, 80, "Funkce může mít nejvýše 80 znaků.", out var contactRole, out error))
            return false;
        if (!TryReadOptional(email, 160, "E-mail může mít nejvýše 160 znaků.", out var contactEmail, out error))
            return false;
        if (!TryReadOptional(phone, 40, "Telefon může mít nejvýše 40 znaků.", out var contactPhone, out error))
            return false;
        if (!TryReadOptional(note, 500, "Poznámka může mít nejvýše 500 znaků.", out var contactNote, out error))
            return false;

        fields = new ContactFields(customer, contactName!, contactRole, contactEmail, contactPhone, contactNote);
        return true;
    }

    private static bool TryReadOpportunity(
        CrmDocument document,
        string? customerId,
        string? title,
        string? stage,
        decimal? amount,
        string? note,
        bool defaultStage,
        out OpportunityFields fields,
        out string? error)
    {
        fields = default;
        if (!TryExistingCustomer(document, customerId, out var customer, out error))
            return false;
        if (!TryReadRequired(title, 160, "Vyplňte název příležitosti.", "Název může mít nejvýše 160 znaků.", out var opportunityTitle, out error))
            return false;
        if (!TryReadStage(stage, defaultStage, out var opportunityStage, out error))
            return false;
        if (!TryReadAmount(amount, out var opportunityAmount, out error))
            return false;
        if (!TryReadOptional(note, 500, "Poznámka může mít nejvýše 500 znaků.", out var opportunityNote, out error))
            return false;

        fields = new OpportunityFields(customer, opportunityTitle!, opportunityStage, opportunityAmount, opportunityNote);
        return true;
    }

    private static bool TryReadActivity(
        CrmDocument document,
        string? kind,
        string? customerId,
        string? opportunityId,
        string? subject,
        string? body,
        DateTimeOffset? occurredAt,
        out ActivityFields fields,
        out string? error)
    {
        fields = default;
        if (!TryReadActivityKind(kind, out var selectedKind, out error))
            return false;
        if (!TryResolveLink(document, customerId, opportunityId, out var customer, out var opportunity, out error))
            return false;
        if (!TryReadRequired(subject, 160, "Vyplňte předmět.", "Předmět může mít nejvýše 160 znaků.", out var activitySubject, out error))
            return false;
        if (!TryReadOptional(body, 1000, "Text může mít nejvýše 1000 znaků.", out var activityBody, out error))
            return false;
        if (occurredAt is null)
        {
            error = "Vyplňte termín.";
            return false;
        }

        fields = new ActivityFields(selectedKind, customer, opportunity, activitySubject!, activityBody, occurredAt.Value);
        return true;
    }

    private static bool TryResolveLink(
        CrmDocument document,
        string? customerId,
        string? opportunityId,
        out string? customer,
        out string? opportunity,
        out string? error)
    {
        var selectedCustomer = BlankToNull(customerId);
        var selectedOpportunity = BlankToNull(opportunityId);
        customer = selectedCustomer;
        opportunity = selectedOpportunity;
        if (selectedCustomer is null && selectedOpportunity is null)
        {
            error = "Aktivita musí být u zákazníka nebo u příležitosti.";
            return false;
        }

        CrmOpportunity? deal = null;
        if (selectedOpportunity is not null)
        {
            deal = document.Opportunities.FirstOrDefault(item => item.Id == selectedOpportunity);
            if (deal is null)
            {
                error = CrmTexts.UnknownOpportunity;
                return false;
            }
        }

        if (selectedCustomer is not null && document.Customers.All(item => item.Id != selectedCustomer))
        {
            error = CrmTexts.UnknownCustomer;
            return false;
        }

        if (deal is not null && customer is null)
            customer = deal.CustomerId;

        if (deal is not null && customer is not null && !string.Equals(deal.CustomerId, customer, StringComparison.Ordinal))
        {
            error = "Zákazník aktivity nesedí se zákazníkem příležitosti.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryExistingCustomer(CrmDocument document, string? customerId, out string id, out string? error)
    {
        var selected = BlankToNull(customerId);
        if (selected is null)
        {
            id = "";
            error = "Vyplňte zákazníka.";
            return false;
        }

        if (document.Customers.All(item => item.Id != selected))
        {
            id = "";
            error = CrmTexts.UnknownCustomer;
            return false;
        }

        id = selected;
        error = null;
        return true;
    }

    private static bool TryReadCustomerKind(string? kind, out string value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            value = "";
            error = "Vyplňte druh zákazníka.";
            return false;
        }

        var selected = kind.Trim();
        if (!CrmCustomerKinds.IsKnown(selected))
        {
            value = "";
            error = CrmCustomerKinds.UnknownMessage;
            return false;
        }

        value = selected;
        error = null;
        return true;
    }

    private static bool TryReadActivityKind(string? kind, out string value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            value = "";
            error = "Vyplňte druh aktivity.";
            return false;
        }

        var selected = kind.Trim();
        if (!CrmActivityKinds.IsKnown(selected))
        {
            value = "";
            error = CrmActivityKinds.UnknownMessage;
            return false;
        }

        value = selected;
        error = null;
        return true;
    }

    private static bool TryReadStage(string? stage, bool defaultWhenEmpty, out string value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            if (defaultWhenEmpty)
            {
                value = CrmStages.Novy;
                error = null;
                return true;
            }

            value = "";
            error = "Vyplňte fázi.";
            return false;
        }

        var selected = stage.Trim();
        if (!CrmStages.IsKnown(selected))
        {
            value = "";
            error = CrmStages.UnknownMessage;
            return false;
        }

        value = selected;
        error = null;
        return true;
    }

    private static bool TryReadAmount(decimal? amount, out decimal? value, out string? error)
    {
        if (amount is null)
        {
            value = null;
            error = null;
            return true;
        }

        if (amount < 0)
        {
            value = null;
            error = "Částka nesmí být záporná.";
            return false;
        }

        var rounded = decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero);
        if (rounded > MaxAmount)
        {
            value = null;
            error = "Částka je příliš vysoká.";
            return false;
        }

        value = rounded;
        error = null;
        return true;
    }

    private static bool TryReadRequired(string? value, int max, string missing, string tooLong, out string? text, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            text = null;
            error = missing;
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > max)
        {
            text = null;
            error = tooLong;
            return false;
        }

        text = trimmed;
        error = null;
        return true;
    }

    private static bool TryReadOptional(string? value, int max, string tooLong, out string? text, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            text = null;
            error = null;
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > max)
        {
            text = null;
            error = tooLong;
            return false;
        }

        text = trimmed;
        error = null;
        return true;
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int IndexOf<T>(List<T> items, Predicate<T> match) => items.FindIndex(match);

    private static string NewId(IEnumerable<string> ids)
    {
        var known = ids.ToArray();
        var id = Guid.NewGuid().ToString("n");
        while (known.Contains(id, StringComparer.Ordinal))
            id = Guid.NewGuid().ToString("n");
        return id;
    }

    private static string NextCode(IEnumerable<string> codes, char prefix, DateTimeOffset now)
    {
        var head = $"{prefix}{now.Year}-";
        var highest = 0;
        foreach (var code in codes)
        {
            if (!code.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(code.AsSpan(head.Length), out var number))
                highest = Math.Max(highest, number);
        }

        return $"{head}{(highest + 1):0000}";
    }

    private readonly record struct CustomerFields(
        string Kind,
        string Name,
        string? Ico,
        string? Email,
        string? Phone,
        string? Street,
        string? City,
        string? Zip,
        string? Country,
        string? Note);

    private readonly record struct ContactFields(
        string CustomerId,
        string Name,
        string? Role,
        string? Email,
        string? Phone,
        string? Note);

    private readonly record struct OpportunityFields(
        string CustomerId,
        string Title,
        string Stage,
        decimal? Amount,
        string? Note);

    private readonly record struct ActivityFields(
        string Kind,
        string? CustomerId,
        string? OpportunityId,
        string Subject,
        string? Body,
        DateTimeOffset OccurredAt);
}
