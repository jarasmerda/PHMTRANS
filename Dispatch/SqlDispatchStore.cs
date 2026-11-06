using Dapper;
using Microsoft.Data.SqlClient;

namespace PHMTRANS.Dispatch;

public sealed class SqlDispatchStore(IConfiguration configuration, ILogger<SqlDispatchStore> logger) : IDispatchStore
{
    private readonly string? _connectionString = configuration.GetConnectionString("Dispatch");
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private int _schemaReady;

    public async Task TryInitializeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            logger.LogWarning("ConnectionStrings:Dispatch není nastavené. Dispečink data neuloží, ERP běží dál.");
            return;
        }

        try
        {
            await EnsureReadyAsync(cancellationToken);
            logger.LogInformation("Schéma dispečinku (tabulky Disp_*) je připravené.");
        }
        catch (DispatchStoreException)
        {
            // EnsureReadyAsync už chybu zapsalo. Start služby kvůli tomu nesmí spadnout.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Schéma dispečinku se nepodařilo založit. ERP běží dál.");
        }
    }

    public Task<DispatchSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        InConnection(async (connection, _, token) =>
        {
            var document = await LoadAsync(connection, transaction: null, token);
            return DispatchRules.Snapshot(document);
        }, cancellationToken);

    public Task<DispatchRateCard> GetRatesAsync(CancellationToken cancellationToken) =>
        InConnection(async (connection, _, token) =>
        {
            var document = await LoadAsync(connection, transaction: null, token);
            return DispatchRules.Rates(document);
        }, cancellationToken);

    public Task<string?> SaveRatesAsync(
        decimal defaultCostPerKm,
        IReadOnlyList<VehicleCostUpdate> vehicles,
        CancellationToken cancellationToken) =>
        InConnection(async (connection, transaction, token) =>
        {
            await TakeLockAsync(connection, transaction!, token);
            var document = await LoadAsync(connection, transaction, token);
            var error = DispatchRules.SaveRates(document, defaultCostPerKm, vehicles);
            if (error is not null)
                return error;

            await connection.ExecuteAsync(Command(
                """
                UPDATE dbo.Disp_Setting
                SET Value = @Value
                WHERE [Key] = N'CostPerKm';
                IF @@ROWCOUNT = 0
                    INSERT INTO dbo.Disp_Setting ([Key], Value) VALUES (N'CostPerKm', @Value);
                """,
                transaction,
                token,
                new { Value = document.DefaultCostPerKm.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            foreach (var vehicle in document.Vehicles)
            {
                await connection.ExecuteAsync(Command(
                    "UPDATE dbo.Disp_Vehicle SET CostPerKm = @CostPerKm WHERE Id = @Id",
                    transaction,
                    token,
                    new { vehicle.Id, vehicle.CostPerKm }));
            }

            await transaction!.CommitAsync(token);
            return (string?)null;
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
            var result = await action(connection, sqlTransaction, cancellationToken);
            return result;
        }
        catch (DispatchStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            logger.LogError(exception, "Dispečink se nepřipojil k SQL Serveru.");
            throw new DispatchStoreException(DispatchStorage.UnavailableMessage, exception);
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new DispatchStoreException(DispatchStorage.MissingConnectionMessage);

        if (Volatile.Read(ref _schemaReady) == 1)
            return;

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady == 1)
                return;

            await DispatchSchema.EnsureAsync(_connectionString, cancellationToken);
            Volatile.Write(ref _schemaReady, 1);
        }
        catch (DispatchStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Dispečink se nepřipojil k SQL Serveru.");
            throw new DispatchStoreException(DispatchStorage.UnavailableMessage, exception);
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
                @Resource = N'PHMTRANS_Disp',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 15000;
            SELECT @result;
            """,
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (result < 0)
            throw new DispatchStoreException("Dispečink právě ukládá jinou změnu, zkuste to znovu.");
    }

    private static async Task<DispatchDocument> LoadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var vehicleRows = (await connection.QueryAsync<VehicleRow>(Command(
            "SELECT Id, Plate, Label, CostPerKm FROM dbo.Disp_Vehicle", transaction, cancellationToken))).AsList();
        var compartments = (await connection.QueryAsync<CompartmentRow>(Command(
            "SELECT VehicleId, Number, CapacityLiters FROM dbo.Disp_VehicleCompartment",
            transaction,
            cancellationToken))).AsList();
        var vehicles = BuildVehicles(vehicleRows, compartments);
        var drivers = (await connection.QueryAsync<DispatchDriver>(Command(
            "SELECT Id, Name, Phone FROM dbo.Disp_Driver", transaction, cancellationToken))).AsList();
        var costText = await connection.ExecuteScalarAsync<string>(Command(
            "SELECT Value FROM dbo.Disp_Setting WHERE [Key] = N'CostPerKm'",
            transaction,
            cancellationToken));
        var defaultCost = decimal.TryParse(costText, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
        return new DispatchDocument
        {
            Vehicles = vehicles,
            Drivers = drivers,
            DefaultCostPerKm = defaultCost
        };
    }

    public static List<DispatchVehicle> BuildVehicles(
        IEnumerable<VehicleRow> rows,
        IEnumerable<CompartmentRow> compartments)
    {
        var byVehicle = compartments
            .Where(row => row.Number is >= 1 and <= VehicleCompartments.MaxCount && row.CapacityLiters > 0m)
            .GroupBy(row => row.VehicleId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VehicleCompartment>)group
                    .OrderBy(row => row.Number)
                    .Select(row => new VehicleCompartment(row.Number, row.CapacityLiters))
                    .ToArray(),
                StringComparer.Ordinal);
        return rows
            .Select(row => new DispatchVehicle(
                row.Id,
                row.Plate,
                row.Label,
                row.CostPerKm,
                byVehicle.TryGetValue(row.Id, out var list) ? list : []))
            .ToList();
    }

    public sealed record VehicleRow(string Id, string Plate, string Label, decimal? CostPerKm);

    public sealed record CompartmentRow(string VehicleId, int Number, decimal CapacityLiters);

    private static CommandDefinition Command(
        string sql,
        SqlTransaction? transaction,
        CancellationToken cancellationToken,
        object? parameters = null) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);
}
