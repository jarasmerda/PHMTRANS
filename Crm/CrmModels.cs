namespace PHMTRANS.Crm;

public static class CrmCustomerKinds
{
    public const string Firma = "Firma";
    public const string Osoba = "Osoba";

    public const string UnknownMessage = "Neznámý druh. Povolené jsou Firma a Osoba.";

    public static readonly IReadOnlyList<string> All = [Firma, Osoba];

    public static bool IsKnown(string kind) => All.Contains(kind);
}

public static class CrmStages
{
    public const string Novy = "Nový";
    public const string Jednani = "V jednání";
    public const string Nabidka = "Nabídka";
    public const string Vyhrano = "Vyhráno";
    public const string Prohrano = "Prohráno";

    public const string UnknownMessage =
        "Neznámá fáze. Povolené jsou Nový, V jednání, Nabídka, Vyhráno a Prohráno.";

    public static readonly IReadOnlyList<string> All = [Novy, Jednani, Nabidka, Vyhrano, Prohrano];

    public static bool IsKnown(string stage) => All.Contains(stage);

    public static int Rank(string stage) => stage switch
    {
        Novy => 0,
        Jednani => 1,
        Nabidka => 2,
        Vyhrano => 3,
        Prohrano => 4,
        _ => 5
    };
}

public static class CrmActivityKinds
{
    public const string Hovor = "Hovor";
    public const string Schuzka = "Schůzka";
    public const string Poznamka = "Poznámka";

    public const string UnknownMessage = "Neznámý druh aktivity. Povolené jsou Hovor, Schůzka a Poznámka.";

    public static readonly IReadOnlyList<string> All = [Hovor, Schuzka, Poznamka];

    public static bool IsKnown(string kind) => All.Contains(kind);
}

public static class CrmTexts
{
    public const string CustomerMissing = "Zákazník neexistuje.";
    public const string ContactMissing = "Kontakt neexistuje.";
    public const string OpportunityMissing = "Příležitost neexistuje.";
    public const string ActivityMissing = "Aktivita neexistuje.";
    public const string UnknownCustomer = "Neznámý zákazník.";
    public const string UnknownOpportunity = "Neznámá příležitost.";
}

public sealed record CrmCustomer(
    string Id,
    string Code,
    string Kind,
    string Name,
    string? Ico,
    string? Email,
    string? Phone,
    string? Street,
    string? City,
    string? Zip,
    string? Country,
    string? Note,
    DateTimeOffset UpdatedAt);

public sealed record CrmContact(
    string Id,
    string CustomerId,
    string Name,
    string? Role,
    string? Email,
    string? Phone,
    string? Note,
    DateTimeOffset UpdatedAt);

public sealed record CrmOpportunity(
    string Id,
    string Code,
    string CustomerId,
    string Title,
    string Stage,
    decimal? Amount,
    DateOnly? ExpectedClose,
    string? Note,
    DateTimeOffset UpdatedAt);

public sealed record CrmActivity(
    string Id,
    string Code,
    string Kind,
    string? CustomerId,
    string? OpportunityId,
    string Subject,
    string? Body,
    DateTimeOffset OccurredAt,
    DateTimeOffset UpdatedAt);

public sealed class CrmDocument
{
    public int Version { get; set; } = 1;

    public string Notice { get; set; } = CrmStorage.Note;

    public List<CrmCustomer> Customers { get; set; } = [];

    public List<CrmContact> Contacts { get; set; } = [];

    public List<CrmOpportunity> Opportunities { get; set; } = [];

    public List<CrmActivity> Activities { get; set; } = [];
}

public sealed record CrmSnapshot(
    IReadOnlyList<CrmCustomer> Customers,
    IReadOnlyList<CrmContact> Contacts,
    IReadOnlyList<CrmOpportunity> Opportunities,
    IReadOnlyList<CrmActivity> Activities);

public sealed record CrmStageCount(string Stage, int Count);

public sealed record CrmKindCount(string Kind, int Count);

public sealed record CrmCounts(
    int Customers,
    int Opportunities,
    int Activities,
    IReadOnlyList<CrmStageCount> Stages,
    IReadOnlyList<CrmKindCount> ActivityKinds);

public sealed record CrmBoard(
    string Storage,
    string StorageNote,
    CrmCounts Counts,
    IReadOnlyList<CrmCustomer> Customers,
    IReadOnlyList<CrmContact> Contacts,
    IReadOnlyList<CrmOpportunity> Opportunities,
    IReadOnlyList<CrmActivity> Activities);

public sealed record CrmCustomerDetail(CrmCustomer Customer, IReadOnlyList<CrmContact> Contacts);

public sealed record NewCustomerRequest(
    string? Kind,
    string? Name,
    string? Ico,
    string? Email,
    string? Phone,
    string? Street,
    string? City,
    string? Zip,
    string? Country,
    string? Note);

public sealed record UpdateCustomerRequest(
    string? Kind,
    string? Name,
    string? Ico,
    string? Email,
    string? Phone,
    string? Street,
    string? City,
    string? Zip,
    string? Country,
    string? Note);

public sealed record NewContactRequest(
    string? CustomerId,
    string? Name,
    string? Role,
    string? Email,
    string? Phone,
    string? Note);

public sealed record UpdateContactRequest(
    string? CustomerId,
    string? Name,
    string? Role,
    string? Email,
    string? Phone,
    string? Note);

public sealed record NewOpportunityRequest(
    string? CustomerId,
    string? Title,
    string? Stage,
    decimal? Amount,
    DateOnly? ExpectedClose,
    string? Note);

public sealed record UpdateOpportunityRequest(
    string? CustomerId,
    string? Title,
    string? Stage,
    decimal? Amount,
    DateOnly? ExpectedClose,
    string? Note);

public sealed record NewActivityRequest(
    string? Kind,
    string? CustomerId,
    string? OpportunityId,
    string? Subject,
    string? Body,
    DateTimeOffset? OccurredAt);

public sealed record UpdateActivityRequest(
    string? Kind,
    string? CustomerId,
    string? OpportunityId,
    string? Subject,
    string? Body,
    DateTimeOffset? OccurredAt);

public abstract record CrmMutation
{
    private CrmMutation()
    {
    }

    public sealed record CustomerSaved(CrmCustomer Customer) : CrmMutation;

    public sealed record ContactSaved(CrmContact Contact) : CrmMutation;

    public sealed record OpportunitySaved(CrmOpportunity Opportunity) : CrmMutation;

    public sealed record ActivitySaved(CrmActivity Activity) : CrmMutation;

    public sealed record NotFound(string Message) : CrmMutation;

    public sealed record Rejected(string Message) : CrmMutation;
}

public sealed class CrmStoreException(string message, Exception? inner = null) : Exception(message, inner);
