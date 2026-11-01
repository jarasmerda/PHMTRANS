namespace PHMTRANS.Crm;

public static class CrmStorage
{
    public const string Kind = "sql";

    public const string Note =
        "Zákazníci, kontakty, příležitosti a aktivity jsou v SQL Serveru (tabulky Crm_*). Do Pohody se nezapisuje.";

    public const string MissingConnectionMessage =
        "CRM nemá nastavené ConnectionStrings:Dispatch. Účetnictví běží dál, zákazníci se neukládají.";

    public const string UnavailableMessage =
        "CRM se nepodařilo načíst z SQL Serveru. Účetnictví běží dál.";
}

public interface ICrmStore
{
    Task<CrmSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task<CrmMutation> AddCustomerAsync(NewCustomerRequest request, CancellationToken cancellationToken);

    Task<CrmMutation> UpdateCustomerAsync(string id, UpdateCustomerRequest request, CancellationToken cancellationToken);

    Task<CrmMutation> AddContactAsync(NewContactRequest request, CancellationToken cancellationToken);

    Task<CrmMutation> UpdateContactAsync(string id, UpdateContactRequest request, CancellationToken cancellationToken);

    Task<CrmMutation> AddOpportunityAsync(NewOpportunityRequest request, CancellationToken cancellationToken);

    Task<CrmMutation> UpdateOpportunityAsync(string id, UpdateOpportunityRequest request, CancellationToken cancellationToken);

    Task<CrmMutation> AddActivityAsync(NewActivityRequest request, CancellationToken cancellationToken);

    Task<CrmMutation> UpdateActivityAsync(string id, UpdateActivityRequest request, CancellationToken cancellationToken);
}
