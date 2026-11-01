using PHMTRANS.Crm;

namespace PHMTRANS.Tests;

public sealed class MemoryCrmStore : ICrmStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CrmDocument _document = CreateSeed();

    public async Task<CrmSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return CrmRules.Snapshot(_document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CrmMutation> AddCustomerAsync(NewCustomerRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => CrmRules.AddCustomer(document, request, now), cancellationToken);

    public Task<CrmMutation> UpdateCustomerAsync(string id, UpdateCustomerRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => CrmRules.UpdateCustomer(document, id, request, now), cancellationToken);

    public Task<CrmMutation> AddContactAsync(NewContactRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => CrmRules.AddContact(document, request, now), cancellationToken);

    public Task<CrmMutation> UpdateContactAsync(string id, UpdateContactRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => CrmRules.UpdateContact(document, id, request, now), cancellationToken);

    public Task<CrmMutation> AddOpportunityAsync(NewOpportunityRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => CrmRules.AddOpportunity(document, request, now), cancellationToken);

    public Task<CrmMutation> UpdateOpportunityAsync(string id, UpdateOpportunityRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => CrmRules.UpdateOpportunity(document, id, request, now), cancellationToken);

    public Task<CrmMutation> AddActivityAsync(NewActivityRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => CrmRules.AddActivity(document, request, now), cancellationToken);

    public Task<CrmMutation> UpdateActivityAsync(string id, UpdateActivityRequest request, CancellationToken cancellationToken) =>
        Change((document, now) => CrmRules.UpdateActivity(document, id, request, now), cancellationToken);

    private async Task<CrmMutation> Change(
        Func<CrmDocument, DateTimeOffset, CrmMutation> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return change(_document, DateTimeOffset.Now);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CrmDocument CreateSeed()
    {
        var now = DateTimeOffset.Now;
        var close = DateOnly.FromDateTime(DateTime.Today.AddDays(21));
        return new CrmDocument
        {
            Customers =
            [
                new("cust-1", "C2026-0001", CrmCustomerKinds.Firma, "Velkoobchod Demo s.r.o.", "12345678", "info@demo.cz", "+420 601 000 001", "Skladová 12", "Cerekvice nad Loučnou", "569 53", "Česká republika", "Ukázková firma", now),
                new("cust-2", "C2026-0002", CrmCustomerKinds.Osoba, "Petra Malá", null, "petra@example.cz", "+420 602 000 002", "Nádražní 3", "Praha", "110 00", "Česká republika", null, now)
            ],
            Contacts =
            [
                new("con-1", "cust-1", "Jan Novák", "nákup", "jan@demo.cz", "+420 603 000 003", null, now),
                new("con-2", "cust-1", "Eva Svobodová", "účetní", "eva@demo.cz", "+420 604 000 004", null, now)
            ],
            Opportunities =
            [
                new("opp-1", "O2026-0001", "cust-1", "Nafta na zimu", CrmStages.Novy, 120000m, close, "Ukázka", now),
                new("opp-2", "O2026-0002", "cust-1", "Rámcová smlouva", CrmStages.Jednani, 450000.50m, close.AddDays(10), null, now.AddHours(-1)),
                new("opp-3", "O2026-0003", "cust-1", "Benzin BA95", CrmStages.Nabidka, 80000m, close.AddDays(5), null, now.AddHours(-2)),
                new("opp-4", "O2026-0004", "cust-1", "Závoz čerpacích stanic", CrmStages.Vyhrano, 900000m, DateOnly.FromDateTime(DateTime.Today.AddDays(-3)), null, now.AddDays(-1)),
                new("opp-5", "O2026-0005", "cust-2", "Jednorázový odběr", CrmStages.Prohrano, 15000m, DateOnly.FromDateTime(DateTime.Today.AddDays(-10)), "Cena", now.AddDays(-2))
            ],
            Activities =
            [
                new("act-1", "A2026-0001", CrmActivityKinds.Hovor, "cust-1", null, "Poptávka nafty", "Voláno na nákup", now.AddHours(-3), now.AddHours(-3)),
                new("act-2", "A2026-0002", CrmActivityKinds.Schuzka, "cust-1", "opp-2", "Schůzka k rámcové smlouvě", null, now.AddHours(-5), now.AddHours(-5)),
                new("act-3", "A2026-0003", CrmActivityKinds.Poznamka, "cust-2", null, "Osobní poznámka", "Zatím bez nabídky", now.AddHours(-8), now.AddHours(-8))
            ]
        };
    }
}
