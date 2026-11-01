using Dapper;
using Microsoft.Data.SqlClient;

namespace PHMTRANS.Crm;

public sealed class SqlCrmStore(IConfiguration configuration, ILogger<SqlCrmStore> logger) : ICrmStore
{
    private readonly string? _connectionString = configuration.GetConnectionString("Dispatch");
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private int _schemaReady;

    public async Task TryInitializeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            logger.LogWarning("ConnectionStrings:Dispatch není nastavené. CRM data neuloží, ERP běží dál.");
            return;
        }

        try
        {
            await EnsureReadyAsync(cancellationToken);
            logger.LogInformation("Schéma CRM (tabulky Crm_*) je připravené.");
        }
        catch (CrmStoreException)
        {
            // EnsureReadyAsync už chybu zapsalo. Start služby kvůli tomu nesmí spadnout.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Schéma CRM se nepodařilo založit. ERP běží dál.");
        }
    }

    public Task<CrmSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        InConnection(async (connection, _, token) =>
        {
            var document = await LoadAsync(connection, transaction: null, token);
            return CrmRules.Snapshot(document);
        }, cancellationToken);

    public Task<CrmMutation> AddCustomerAsync(NewCustomerRequest request, CancellationToken cancellationToken) =>
        ChangeAsync((document, now) => CrmRules.AddCustomer(document, request, now), cancellationToken);

    public Task<CrmMutation> UpdateCustomerAsync(string id, UpdateCustomerRequest request, CancellationToken cancellationToken) =>
        ChangeAsync((document, now) => CrmRules.UpdateCustomer(document, id, request, now), cancellationToken);

    public Task<CrmMutation> AddContactAsync(NewContactRequest request, CancellationToken cancellationToken) =>
        ChangeAsync((document, now) => CrmRules.AddContact(document, request, now), cancellationToken);

    public Task<CrmMutation> UpdateContactAsync(string id, UpdateContactRequest request, CancellationToken cancellationToken) =>
        ChangeAsync((document, now) => CrmRules.UpdateContact(document, id, request, now), cancellationToken);

    public Task<CrmMutation> AddOpportunityAsync(NewOpportunityRequest request, CancellationToken cancellationToken) =>
        ChangeAsync((document, now) => CrmRules.AddOpportunity(document, request, now), cancellationToken);

    public Task<CrmMutation> UpdateOpportunityAsync(string id, UpdateOpportunityRequest request, CancellationToken cancellationToken) =>
        ChangeAsync((document, now) => CrmRules.UpdateOpportunity(document, id, request, now), cancellationToken);

    public Task<CrmMutation> AddActivityAsync(NewActivityRequest request, CancellationToken cancellationToken) =>
        ChangeAsync((document, now) => CrmRules.AddActivity(document, request, now), cancellationToken);

    public Task<CrmMutation> UpdateActivityAsync(string id, UpdateActivityRequest request, CancellationToken cancellationToken) =>
        ChangeAsync((document, now) => CrmRules.UpdateActivity(document, id, request, now), cancellationToken);

    private async Task<CrmMutation> ChangeAsync(
        Func<CrmDocument, DateTimeOffset, CrmMutation> change,
        CancellationToken cancellationToken) =>
        await InConnection(async (connection, transaction, token) =>
        {
            await TakeLockAsync(connection, transaction!, token);
            var document = await LoadAsync(connection, transaction, token);
            var knownCustomers = document.Customers.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var knownContacts = document.Contacts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var knownOpportunities = document.Opportunities.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var knownActivities = document.Activities.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var mutation = change(document, DateTimeOffset.Now);
            switch (mutation)
            {
                case CrmMutation.CustomerSaved saved:
                    if (knownCustomers.Contains(saved.Customer.Id))
                        await UpdateCustomerAsync(connection, transaction!, saved.Customer, token);
                    else
                        await InsertCustomerAsync(connection, transaction!, saved.Customer, token);
                    break;
                case CrmMutation.ContactSaved saved:
                    if (knownContacts.Contains(saved.Contact.Id))
                        await UpdateContactAsync(connection, transaction!, saved.Contact, token);
                    else
                        await InsertContactAsync(connection, transaction!, saved.Contact, token);
                    break;
                case CrmMutation.OpportunitySaved saved:
                    if (knownOpportunities.Contains(saved.Opportunity.Id))
                        await UpdateOpportunityAsync(connection, transaction!, saved.Opportunity, token);
                    else
                        await InsertOpportunityAsync(connection, transaction!, saved.Opportunity, token);
                    break;
                case CrmMutation.ActivitySaved saved:
                    if (knownActivities.Contains(saved.Activity.Id))
                        await UpdateActivityAsync(connection, transaction!, saved.Activity, token);
                    else
                        await InsertActivityAsync(connection, transaction!, saved.Activity, token);
                    break;
                default:
                    return mutation;
            }

            await transaction!.CommitAsync(token);
            return mutation;
        }, cancellationToken, transaction: true);

    private async Task<T> InConnection<T>(
        Func<SqlConnection, SqlTransaction?, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        bool transaction = false)
    {
        await EnsureReadyAsync(cancellationToken);
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            if (!transaction)
                return await action(connection, null, cancellationToken);

            await using var dbTransaction = await connection.BeginTransactionAsync(cancellationToken);
            var sqlTransaction = (SqlTransaction)dbTransaction;
            return await action(connection, sqlTransaction, cancellationToken);
        }
        catch (CrmStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            logger.LogError(exception, "CRM se nepřipojilo k SQL Serveru.");
            throw new CrmStoreException(CrmStorage.UnavailableMessage, exception);
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new CrmStoreException(CrmStorage.MissingConnectionMessage);

        if (Volatile.Read(ref _schemaReady) == 1)
            return;

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady == 1)
                return;

            await CrmSchema.EnsureAsync(_connectionString, cancellationToken);
            Volatile.Write(ref _schemaReady, 1);
        }
        catch (CrmStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "CRM se nepřipojilo k SQL Serveru.");
            throw new CrmStoreException(CrmStorage.UnavailableMessage, exception);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static async Task TakeLockAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            DECLARE @result int;
            EXEC @result = sp_getapplock
                @Resource = N'PHMTRANS_Crm',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 15000;
            SELECT @result;
            """,
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (result < 0)
            throw new CrmStoreException("CRM právě ukládá jinou změnu, zkuste to znovu.");
    }

    private static async Task<CrmDocument> LoadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var customers = (await connection.QueryAsync<CrmCustomer>(Command(
            """
            SELECT Id, Code, Kind, Name, Ico, Email, Phone, Street, City, Zip, Country, Note, UpdatedAt
            FROM dbo.Crm_Customer
            """,
            transaction,
            cancellationToken))).AsList();
        var contacts = (await connection.QueryAsync<CrmContact>(Command(
            """
            SELECT Id, CustomerId, Name, Role, Email, Phone, Note, UpdatedAt
            FROM dbo.Crm_Contact
            """,
            transaction,
            cancellationToken))).AsList();
        var opportunityRows = (await connection.QueryAsync<OpportunityRow>(Command(
            """
            SELECT Id, Code, CustomerId, Title, Stage, Amount, ExpectedClose, Note, UpdatedAt
            FROM dbo.Crm_Opportunity
            """,
            transaction,
            cancellationToken))).AsList();
        var activities = (await connection.QueryAsync<CrmActivity>(Command(
            """
            SELECT Id, Code, Kind, CustomerId, OpportunityId, Subject, Body, OccurredAt, UpdatedAt
            FROM dbo.Crm_Activity
            """,
            transaction,
            cancellationToken))).AsList();
        return new CrmDocument
        {
            Customers = customers,
            Contacts = contacts,
            Opportunities = opportunityRows.Select(row => row.ToModel()).ToList(),
            Activities = activities
        };
    }

    private static Task<int> InsertCustomerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CrmCustomer customer,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            INSERT INTO dbo.Crm_Customer
                (Id, Code, Kind, Name, Ico, Email, Phone, Street, City, Zip, Country, Note, UpdatedAt)
            VALUES
                (@Id, @Code, @Kind, @Name, @Ico, @Email, @Phone, @Street, @City, @Zip, @Country, @Note, @UpdatedAt)
            """,
            transaction,
            cancellationToken,
            customer));

    private static Task<int> UpdateCustomerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CrmCustomer customer,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            UPDATE dbo.Crm_Customer
            SET Kind = @Kind,
                Name = @Name,
                Ico = @Ico,
                Email = @Email,
                Phone = @Phone,
                Street = @Street,
                City = @City,
                Zip = @Zip,
                Country = @Country,
                Note = @Note,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """,
            transaction,
            cancellationToken,
            customer));

    private static Task<int> InsertContactAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CrmContact contact,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            INSERT INTO dbo.Crm_Contact
                (Id, CustomerId, Name, Role, Email, Phone, Note, UpdatedAt)
            VALUES
                (@Id, @CustomerId, @Name, @Role, @Email, @Phone, @Note, @UpdatedAt)
            """,
            transaction,
            cancellationToken,
            contact));

    private static Task<int> UpdateContactAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CrmContact contact,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            UPDATE dbo.Crm_Contact
            SET CustomerId = @CustomerId,
                Name = @Name,
                Role = @Role,
                Email = @Email,
                Phone = @Phone,
                Note = @Note,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """,
            transaction,
            cancellationToken,
            contact));

    private static Task<int> InsertOpportunityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CrmOpportunity opportunity,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            INSERT INTO dbo.Crm_Opportunity
                (Id, Code, CustomerId, Title, Stage, Amount, ExpectedClose, Note, UpdatedAt)
            VALUES
                (@Id, @Code, @CustomerId, @Title, @Stage, @Amount, @ExpectedClose, @Note, @UpdatedAt)
            """,
            transaction,
            cancellationToken,
            OpportunityParameters(opportunity)));

    private static Task<int> UpdateOpportunityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CrmOpportunity opportunity,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            UPDATE dbo.Crm_Opportunity
            SET CustomerId = @CustomerId,
                Title = @Title,
                Stage = @Stage,
                Amount = @Amount,
                ExpectedClose = @ExpectedClose,
                Note = @Note,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """,
            transaction,
            cancellationToken,
            OpportunityParameters(opportunity)));

    private static Task<int> InsertActivityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CrmActivity activity,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            INSERT INTO dbo.Crm_Activity
                (Id, Code, Kind, CustomerId, OpportunityId, Subject, Body, OccurredAt, UpdatedAt)
            VALUES
                (@Id, @Code, @Kind, @CustomerId, @OpportunityId, @Subject, @Body, @OccurredAt, @UpdatedAt)
            """,
            transaction,
            cancellationToken,
            activity));

    private static Task<int> UpdateActivityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CrmActivity activity,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            UPDATE dbo.Crm_Activity
            SET Kind = @Kind,
                CustomerId = @CustomerId,
                OpportunityId = @OpportunityId,
                Subject = @Subject,
                Body = @Body,
                OccurredAt = @OccurredAt,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """,
            transaction,
            cancellationToken,
            activity));

    private static object OpportunityParameters(CrmOpportunity opportunity) => new
    {
        opportunity.Id,
        opportunity.Code,
        opportunity.CustomerId,
        opportunity.Title,
        opportunity.Stage,
        opportunity.Amount,
        ExpectedClose = opportunity.ExpectedClose?.ToDateTime(TimeOnly.MinValue),
        opportunity.Note,
        opportunity.UpdatedAt
    };

    private static CommandDefinition Command(
        string sql,
        SqlTransaction? transaction,
        CancellationToken cancellationToken,
        object? parameters = null) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private sealed class OpportunityRow
    {
        public string Id { get; set; } = "";

        public string Code { get; set; } = "";

        public string CustomerId { get; set; } = "";

        public string Title { get; set; } = "";

        public string Stage { get; set; } = "";

        public decimal? Amount { get; set; }

        public DateTime? ExpectedClose { get; set; }

        public string? Note { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public CrmOpportunity ToModel() => new(
            Id,
            Code,
            CustomerId,
            Title,
            Stage,
            Amount,
            ExpectedClose is { } day ? DateOnly.FromDateTime(day) : null,
            Note,
            UpdatedAt);
    }
}
