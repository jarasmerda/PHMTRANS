using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Dispatch;

public sealed class SqlOrderStore(
    IConfiguration configuration,
    IOptions<PohodaOptions> options,
    ILogger<SqlOrderStore> logger) : IOrderStore
{
    private readonly string? _connectionString = configuration.GetConnectionString("Dispatch");
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private int _schemaReady;

    static SqlOrderStore()
    {
        try
        {
            SqlMapper.AddTypeHandler(new DateOnlyHandler());
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Handler už v procesu je. Datum se dál čte stejně.
        }
    }

    public Task<IReadOnlyList<DispatchOrder>> ListAsync(CancellationToken cancellationToken) =>
        InConnection(async (connection, _, token) =>
        {
            var rows = await connection.QueryAsync<OrderRow>(Command(
                """
                SELECT
                    o.Id, o.Code, o.CustomerPohodaId, o.CustomerName, o.LoadingPlace, o.UnloadingPlace,
                    o.LoadingDate, o.UnloadingDate, o.FuelType, o.Liters, o.AgreedPrice, o.Surcharges,
                    o.Note, o.CmrNumber,
                    CASE WHEN o.TripId IS NULL THEN o.VehicleId ELSE t.VehicleId END AS VehicleId,
                    CASE WHEN o.TripId IS NULL THEN o.DriverId ELSE t.DriverId END AS DriverId,
                    o.TripId, o.Status, o.IsLocked,
                    o.CurrentVersion, o.PohodaOrderId, o.PohodaOrderNumber, o.PohodaInvoiceNumber,
                    o.LastSentVersion, o.CreatedAt, o.UpdatedAt,
                    o.PricingMode, o.SalePerKm, o.SalePerLiter, o.CostPerKm,
                    o.CancelReason, o.CustomerIco, o.CustomerDic, o.CustomerStreet, o.CustomerCity, o.CustomerZip, o.CustomerEmail, o.CustomerPhone,
                    q.Status AS LatestSyncStatus, q.LastError AS LatestSyncError, q.Warning AS LatestSyncWarning
                FROM dbo.Disp_Order o
                LEFT JOIN dbo.Disp_Trip t ON t.Id = o.TripId
                OUTER APPLY (
                    SELECT TOP (1) Status, LastError, Warning
                    FROM dbo.Disp_SyncQueue
                    WHERE OrderId = o.Id
                    ORDER BY Id DESC
                ) q
                """,
                transaction: null,
                token));
            var list = rows.Select(row => row.ToOrder()).ToArray();
            return await AttachRoutesAsync(connection, list, token);
        }, cancellationToken);

    public Task<OrderBundle?> GetAsync(string id, CancellationToken cancellationToken) =>
        InConnection(async (connection, _, token) =>
        {
            var order = await LoadOrderAsync(connection, null, id, token);
            if (order is null)
                return null;

            order = await WithRouteAsync(connection, null, order, token);
            var versions = await LoadVersionsAsync(connection, null, order, token);
            var sync = await LoadQueueAsync(connection, null, id, token);
            return new OrderBundle(WithLatestSync(order, sync), versions, sync);
        }, cancellationToken);

    public Task<OrderMutation> CreateAsync(OrderSaveRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(null, (context, _) => OrderWorkflow.Create(request, context), cancellationToken);

    public Task<OrderMutation> UpdateAsync(string id, OrderSaveRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(id, (context, _) => OrderWorkflow.Save(request, context), cancellationToken);

    public Task<OrderMutation> ConfirmAsync(string id, CancellationToken cancellationToken) =>
        ChangeAsync(id, (context, _) => OrderWorkflow.Confirm(context), cancellationToken);

    public Task<OrderMutation> MarkInvoicedAsync(string id, string? invoiceNumber, CancellationToken cancellationToken) =>
        ChangeAsync(id, (context, _) => OrderWorkflow.MarkInvoiced(invoiceNumber, context), cancellationToken);

    public Task<OrderMutation> ResendAsync(string id, CancellationToken cancellationToken) =>
        ChangeAsync(id, (context, latest) => OrderWorkflow.Resend(latest, context), cancellationToken);

    public Task<OrderMutation> CancelAsync(string id, string? reason, bool delete, CancellationToken cancellationToken) =>
        ChangeAsync(id, (context, _) => OrderWorkflow.Cancel(reason, delete, context), cancellationToken);

    public Task<OrderMutation> AttachTripAsync(string orderId, string? tripId, bool onlyIfFree, CancellationToken cancellationToken) =>
        ChangeAsync(orderId, (context, _) => OrderWorkflow.AssignTrip(tripId, onlyIfFree, context), cancellationToken);

    public async Task<string?> AttachOrdersAsync(
        string tripId,
        IReadOnlyList<string> orderIds,
        bool onlyIfFree,
        CancellationToken cancellationToken)
    {
        foreach (var orderId in orderIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var result = await AttachTripAsync(orderId, tripId, onlyIfFree, cancellationToken);
            if (result is OrderMutation.Rejected rejected)
                return rejected.Message;
            if (result is OrderMutation.NotFound)
                return "Zakázka neexistuje.";
        }

        return null;
    }

    public async Task<IReadOnlyList<OrderSyncEntry>> ListQueueAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new OrderStoreException(DispatchStorage.MissingConnectionMessage);

        return await InConnection(async (connection, _, token) =>
        {
            var rows = await connection.QueryAsync<QueueRow>(Command(
                QueueSql + " ORDER BY q.Id DESC",
                transaction: null,
                token));
            return (IReadOnlyList<OrderSyncEntry>)rows.Select(row => row.ToEntry()).ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<OrderSyncEntry>> ListDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            return [];

        try
        {
            return await InConnection(async (connection, _, token) =>
            {
                var rows = await connection.QueryAsync<QueueRow>(Command(
                    QueueSql + " WHERE q.Status = N'Pending' ORDER BY q.Id",
                    transaction: null,
                    token));
                return (IReadOnlyList<OrderSyncEntry>)rows
                    .Select(row => row.ToEntry())
                    .Where(entry => OrderSyncLimits.IsDue(entry.ProcessedUtc, entry.Attempts, now))
                    .Take(Math.Max(1, take))
                    .ToArray();
            }, cancellationToken);
        }
        catch (OrderStoreException exception)
        {
            logger.LogWarning(exception, "Fronta zakázek se nepodařila načíst.");
            return [];
        }
    }

    public Task MarkSentAsync(OrderSyncEntry entry, PohodaImportResult result, CancellationToken cancellationToken) =>
        InConnection(async (connection, transaction, token) =>
        {
            var now = DateTimeOffset.UtcNow;
            var updated = await connection.ExecuteAsync(Command(
                """
                UPDATE dbo.Disp_SyncQueue
                SET Status = N'Sent',
                    ProcessedUtc = @Now,
                    PohodaDocNumber = @Number,
                    LastError = NULL
                WHERE Id = @Id AND Status = N'Pending' AND VersionTo = @VersionTo
                """,
                transaction,
                token,
                new { entry.Id, entry.VersionTo, Now = now, Number = Trim(result.Number, 40) }));
            if (updated == 0)
            {
                logger.LogWarning(
                    "Doklad Pohody {Number} se vrátil, ale položka fronty {QueueId} už mezitím změnila verzi.",
                    result.Number,
                    entry.Id);
                return 0;
            }

            await connection.ExecuteAsync(Command(
                """
                UPDATE dbo.Disp_Order
                SET LastSentVersion = @VersionTo,
                    PohodaOrderId = COALESCE(@PohodaId, PohodaOrderId),
                    PohodaOrderNumber = COALESCE(@Number, PohodaOrderNumber),
                    UpdatedAt = @Now
                WHERE Id = @OrderId
                """,
                transaction,
                token,
                new
                {
                    entry.OrderId,
                    entry.VersionTo,
                    PohodaId = result.Id,
                    Number = Trim(result.Number, 40),
                    Now = now
                }));
            await connection.ExecuteAsync(Command(
                """
                UPDATE dbo.Disp_OrderVersion
                SET PohodaOrderId = COALESCE(@PohodaId, PohodaOrderId),
                    PohodaOrderNumber = COALESCE(@Number, PohodaOrderNumber)
                WHERE OrderId = @OrderId AND Version = @VersionTo
                """,
                transaction,
                token,
                new
                {
                    entry.OrderId,
                    entry.VersionTo,
                    PohodaId = result.Id,
                    Number = Trim(result.Number, 40)
                }));
            await transaction!.CommitAsync(token);
            return updated;
        }, cancellationToken, transaction: true);

    public Task MarkFailedAsync(long id, string error, CancellationToken cancellationToken) =>
        InConnection(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(Command(
                """
                UPDATE dbo.Disp_SyncQueue
                SET Attempts = Attempts + 1,
                    LastError = @LastError,
                    ProcessedUtc = @Now,
                    Status = CASE WHEN Attempts + 1 >= @Max THEN N'Failed' ELSE N'Pending' END
                WHERE Id = @Id AND Status = N'Pending'
                """,
                transaction,
                token,
                new
                {
                    Id = id,
                    LastError = Trim(error, OrderSyncLimits.LastErrorMaxLength),
                    Now = DateTimeOffset.UtcNow,
                    Max = OrderSyncLimits.MaxAttempts
                }));
            await transaction!.CommitAsync(token);
            return 0;
        }, cancellationToken, transaction: true);

    public Task SavePayloadAsync(long id, string payloadXml, CancellationToken cancellationToken) =>
        InConnection(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(Command(
                """
                UPDATE dbo.Disp_SyncQueue
                SET PayloadXml = @PayloadXml
                WHERE Id = @Id AND Status = N'Pending'
                """,
                transaction,
                token,
                new { Id = id, PayloadXml = payloadXml }));
            await transaction!.CommitAsync(token);
            return 0;
        }, cancellationToken, transaction: true);

    public Task MarkDeleteCommittedAsync(
        OrderSyncEntry entry,
        string error,
        string addOnlyXml,
        CancellationToken cancellationToken) =>
        InConnection(async (connection, transaction, token) =>
        {
            var now = DateTimeOffset.UtcNow;
            await connection.ExecuteAsync(Command(
                """
                UPDATE dbo.Disp_SyncQueue
                SET PayloadXml = @PayloadXml,
                    Attempts = Attempts + 1,
                    LastError = @LastError,
                    ProcessedUtc = @Now,
                    Status = CASE WHEN Attempts + 1 >= @Max THEN N'Failed' ELSE N'Pending' END
                WHERE Id = @Id AND Status = N'Pending'
                """,
                transaction,
                token,
                new
                {
                    entry.Id,
                    PayloadXml = addOnlyXml,
                    LastError = Trim(error, OrderSyncLimits.LastErrorMaxLength),
                    Now = now,
                    Max = OrderSyncLimits.MaxAttempts
                }));
            await connection.ExecuteAsync(Command(
                """
                UPDATE dbo.Disp_Order
                SET PohodaOrderId = NULL,
                    UpdatedAt = @Now
                WHERE Id = @OrderId
                """,
                transaction,
                token,
                new { entry.OrderId, Now = now }));
            await transaction!.CommitAsync(token);
            return 0;
        }, cancellationToken, transaction: true);

    private async Task<OrderMutation> ChangeAsync(
        string? id,
        Func<OrderEditContext, OrderSyncEntry?, OrderChange> change,
        CancellationToken cancellationToken) =>
        await InConnection(async (connection, transaction, token) =>
        {
            await TakeLockAsync(connection, transaction!, token);
            var catalog = await LoadCatalogAsync(connection, transaction, token);
            var codes = (await connection.QueryAsync<string>(Command(
                "SELECT Code FROM dbo.Disp_Order",
                transaction,
                token))).AsList();

            DispatchOrder? current = null;
            DispatchOrder? lastSent = null;
            OrderSyncEntry? latest = null;
            if (id is not null)
            {
                current = await LoadOrderAsync(connection, transaction, id, token);
                if (current is not null)
                    current = await WithRouteAsync(connection, transaction, current, token);
                if (current is null)
                {
                    await transaction!.CommitAsync(token);
                    return (OrderMutation)new OrderMutation.NotFound();
                }

                if (current.LastSentVersion is int sentVersion)
                    lastSent = await LoadVersionAsync(connection, transaction, current, sentVersion, token);
                latest = (await LoadQueueAsync(connection, transaction, id, token)).FirstOrDefault();
            }

            var sentCorrections = id is null
                ? 0
                : await connection.ExecuteScalarAsync<int>(Command(
                    """
                    SELECT COUNT(*)
                    FROM dbo.Disp_SyncQueue
                    WHERE OrderId = @OrderId AND Action IN (N'Correction', N'Cancel') AND Status = N'Sent'
                    """,
                    transaction,
                    token,
                    new { OrderId = id }));
            var context = new OrderEditContext
            {
                Catalog = catalog,
                SyncMode = options.Value.EffectiveSyncMode(),
                Pohoda = options.Value,
                Now = DateTimeOffset.Now,
                ExistingCodes = codes,
                Current = current,
                LastSent = lastSent,
                SentCorrections = sentCorrections
            };
            var result = change(context, latest);
            if (result is OrderChange.Deleted deleted)
            {
                await DeleteOrderAsync(connection, transaction!, deleted.Id, token);
                await transaction!.CommitAsync(token);
                return (OrderMutation)new OrderMutation.Deleted(deleted.Id);
            }

            if (result is not OrderChange.Saved saved)
            {
                await transaction!.CommitAsync(token);
                return new OrderMutation.Rejected(result is OrderChange.Rejected rejected ? rejected.Message : "Zakázku se nepodařilo uložit.");
            }

            var order = ApplyQueueHint(saved);
            if (current is null)
                await InsertOrderAsync(connection, transaction!, order, token);
            else
                await UpdateOrderAsync(connection, transaction!, order, token);

            if (order.Stops is not null)
                await ReplaceRouteAsync(connection, transaction!, order, token);
            if (saved.VersionAdded)
                await InsertVersionAsync(connection, transaction!, order, token);
            if (saved.DropOpenQueue)
                await DropOpenQueueAsync(connection, transaction!, order.Id, token);
            if (saved.Queue is not null)
                await UpsertQueueAsync(connection, transaction!, order, saved.Queue, token);

            await transaction!.CommitAsync(token);
            return (OrderMutation)new OrderMutation.Success(order);
        }, cancellationToken, transaction: true);

    private static DispatchOrder ApplyQueueHint(OrderChange.Saved saved)
    {
        if (saved.Queue is null)
            return saved.Order;
        return saved.Order with
        {
            LatestSyncStatus = saved.Queue.Status,
            LatestSyncError = null,
            LatestSyncWarning = saved.Queue.Warning
        };
    }

    private static DispatchOrder WithLatestSync(DispatchOrder order, IReadOnlyList<OrderSyncEntry> sync)
    {
        var latest = sync.FirstOrDefault();
        if (latest is null)
            return order;
        return order with
        {
            LatestSyncStatus = latest.Status,
            LatestSyncError = latest.LastError,
            LatestSyncWarning = latest.Warning
        };
    }

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
        catch (OrderStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            logger.LogError(exception, "Zakázky se nepřipojily k SQL Serveru.");
            throw new OrderStoreException(DispatchStorage.UnavailableMessage, exception);
        }
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new OrderStoreException(DispatchStorage.MissingConnectionMessage);

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
        catch (Exception exception) when (exception is not OperationCanceledException and not OrderStoreException)
        {
            logger.LogError(exception, "Schéma zakázek se nepodařilo založit.");
            throw new OrderStoreException(DispatchStorage.UnavailableMessage, exception);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static async Task TakeLockAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var result = await connection.ExecuteScalarAsync<int>(Command(
            """
            DECLARE @result int;
            EXEC @result = sp_getapplock
                @Resource = N'PHMTRANS_Disp',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 15000;
            SELECT @result;
            """,
            transaction,
            cancellationToken));
        if (result < 0)
            throw new OrderStoreException("Dispečink právě ukládá jinou změnu, zkuste to znovu.");
    }

    private static async Task<OrderCatalog> LoadCatalogAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var vehicles = (await connection.QueryAsync<VehicleCostRow>(Command(
            "SELECT Id, CostPerKm FROM dbo.Disp_Vehicle", transaction, cancellationToken))).AsList();
        var drivers = await connection.QueryAsync<string>(Command("SELECT Id FROM dbo.Disp_Driver", transaction, cancellationToken));
        var trips = (await connection.QueryAsync<TripLink>(Command(
            "SELECT Id, VehicleId, DriverId, Status FROM dbo.Disp_Trip", transaction, cancellationToken))).AsList();
        var costText = await connection.ExecuteScalarAsync<string>(Command(
            "SELECT Value FROM dbo.Disp_Setting WHERE [Key] = N'CostPerKm'",
            transaction,
            cancellationToken));
        var defaultCost = decimal.TryParse(costText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
        return new OrderCatalog(
            vehicles.Select(vehicle => vehicle.Id).ToHashSet(StringComparer.Ordinal),
            drivers.ToHashSet(StringComparer.Ordinal),
            trips.Select(trip => trip.Id).ToHashSet(StringComparer.Ordinal),
            trips.ToDictionary(trip => trip.Id, StringComparer.Ordinal),
            defaultCost,
            vehicles.Where(vehicle => vehicle.CostPerKm is not null)
                .ToDictionary(vehicle => vehicle.Id, vehicle => vehicle.CostPerKm!.Value, StringComparer.Ordinal));
    }

    private static async Task<DispatchOrder?> LoadOrderAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string id,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<OrderRow>(Command(
            """
            SELECT
                o.Id, o.Code, o.CustomerPohodaId, o.CustomerName, o.LoadingPlace, o.UnloadingPlace,
                o.LoadingDate, o.UnloadingDate, o.FuelType, o.Liters, o.AgreedPrice, o.Surcharges,
                o.Note, o.CmrNumber,
                CASE WHEN o.TripId IS NULL THEN o.VehicleId ELSE t.VehicleId END AS VehicleId,
                CASE WHEN o.TripId IS NULL THEN o.DriverId ELSE t.DriverId END AS DriverId,
                o.TripId, o.Status, o.IsLocked,
                o.CurrentVersion, o.PohodaOrderId, o.PohodaOrderNumber, o.PohodaInvoiceNumber,
                o.LastSentVersion, o.CreatedAt, o.UpdatedAt,
                o.PricingMode, o.SalePerKm, o.SalePerLiter, o.CostPerKm,
                o.CancelReason, o.CustomerIco, o.CustomerDic, o.CustomerStreet, o.CustomerCity, o.CustomerZip, o.CustomerEmail, o.CustomerPhone
            FROM dbo.Disp_Order o
            LEFT JOIN dbo.Disp_Trip t ON t.Id = o.TripId
            WHERE o.Id = @Id
            """,
            transaction,
            cancellationToken,
            new { Id = id }));
        return row?.ToOrder();
    }

    private static async Task<IReadOnlyList<DispatchOrder>> LoadVersionsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        DispatchOrder order,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<VersionRow>(Command(
            """
            SELECT
                Version, CustomerPohodaId, CustomerName, LoadingPlace, UnloadingPlace,
                LoadingDate, UnloadingDate, FuelType, Liters, AgreedPrice, Surcharges,
                Note, CmrNumber, VehicleId, DriverId, TripId, Status, IsLocked,
                PohodaOrderId, PohodaOrderNumber, PohodaInvoiceNumber, CreatedUtc,
                PricingMode, SalePerKm, SalePerLiter, CostPerKm, RouteJson,
                CancelReason, CustomerIco, CustomerDic, CustomerStreet, CustomerCity, CustomerZip, CustomerEmail, CustomerPhone
            FROM dbo.Disp_OrderVersion
            WHERE OrderId = @Id
            ORDER BY Version DESC
            """,
            transaction,
            cancellationToken,
            new { order.Id }));
        return rows.Select(row => row.ToOrder(order)).ToArray();
    }

    private static async Task<DispatchOrder?> LoadVersionAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        DispatchOrder order,
        int version,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<VersionRow>(Command(
            """
            SELECT
                Version, CustomerPohodaId, CustomerName, LoadingPlace, UnloadingPlace,
                LoadingDate, UnloadingDate, FuelType, Liters, AgreedPrice, Surcharges,
                Note, CmrNumber, VehicleId, DriverId, TripId, Status, IsLocked,
                PohodaOrderId, PohodaOrderNumber, PohodaInvoiceNumber, CreatedUtc,
                PricingMode, SalePerKm, SalePerLiter, CostPerKm, RouteJson,
                CancelReason, CustomerIco, CustomerDic, CustomerStreet, CustomerCity, CustomerZip, CustomerEmail, CustomerPhone
            FROM dbo.Disp_OrderVersion
            WHERE OrderId = @Id AND Version = @Version
            """,
            transaction,
            cancellationToken,
            new { order.Id, Version = version }));
        return row?.ToOrder(order);
    }

    private static async Task<IReadOnlyList<OrderSyncEntry>> LoadQueueAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string orderId,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<QueueRow>(Command(
            QueueSql + " WHERE q.OrderId = @OrderId ORDER BY q.Id DESC",
            transaction,
            cancellationToken,
            new { OrderId = orderId }));
        return rows.Select(row => row.ToEntry()).ToArray();
    }

    private static Task<int> InsertOrderAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DispatchOrder order,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            INSERT INTO dbo.Disp_Order
                (Id, Code, CustomerPohodaId, CustomerName, LoadingPlace, UnloadingPlace, LoadingDate, UnloadingDate,
                 FuelType, Liters, AgreedPrice, Surcharges, Note, CmrNumber, VehicleId, DriverId, TripId, Status,
                 IsLocked, CurrentVersion, PohodaOrderId, PohodaOrderNumber, PohodaInvoiceNumber, LastSentVersion,
                 CreatedAt, UpdatedAt, PricingMode, SalePerKm, SalePerLiter, CostPerKm,
                 CancelReason, CustomerIco, CustomerDic, CustomerStreet, CustomerCity, CustomerZip, CustomerEmail, CustomerPhone)
            VALUES
                (@Id, @Code, @CustomerPohodaId, @CustomerName, @LoadingPlace, @UnloadingPlace, @LoadingDate, @UnloadingDate,
                 @FuelType, @Liters, @AgreedPrice, @Surcharges, @Note, @CmrNumber, @VehicleId, @DriverId, @TripId, @Status,
                 @IsLocked, @CurrentVersion, @PohodaOrderId, @PohodaOrderNumber, @PohodaInvoiceNumber, @LastSentVersion,
                 @CreatedAt, @UpdatedAt, @PricingMode, @SalePerKm, @SalePerLiter, @CostPerKm,
                 @CancelReason, @CustomerIco, @CustomerDic, @CustomerStreet, @CustomerCity, @CustomerZip, @CustomerEmail, @CustomerPhone)
            """,
            transaction,
            cancellationToken,
            order));

    private static Task<int> UpdateOrderAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DispatchOrder order,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            UPDATE dbo.Disp_Order
            SET CustomerPohodaId = @CustomerPohodaId,
                CustomerName = @CustomerName,
                LoadingPlace = @LoadingPlace,
                UnloadingPlace = @UnloadingPlace,
                LoadingDate = @LoadingDate,
                UnloadingDate = @UnloadingDate,
                FuelType = @FuelType,
                Liters = @Liters,
                AgreedPrice = @AgreedPrice,
                Surcharges = @Surcharges,
                Note = @Note,
                CmrNumber = @CmrNumber,
                VehicleId = @VehicleId,
                DriverId = @DriverId,
                TripId = @TripId,
                Status = @Status,
                IsLocked = @IsLocked,
                CurrentVersion = @CurrentVersion,
                PohodaOrderId = @PohodaOrderId,
                PohodaOrderNumber = @PohodaOrderNumber,
                PohodaInvoiceNumber = @PohodaInvoiceNumber,
                LastSentVersion = @LastSentVersion,
                PricingMode = @PricingMode,
                SalePerKm = @SalePerKm,
                SalePerLiter = @SalePerLiter,
                CostPerKm = @CostPerKm,
                CancelReason = @CancelReason,
                CustomerIco = @CustomerIco,
                CustomerDic = @CustomerDic,
                CustomerStreet = @CustomerStreet,
                CustomerCity = @CustomerCity,
                CustomerZip = @CustomerZip,
                CustomerEmail = @CustomerEmail,
                CustomerPhone = @CustomerPhone,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """,
            transaction,
            cancellationToken,
            order));

    private static Task<int> InsertVersionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DispatchOrder order,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            INSERT INTO dbo.Disp_OrderVersion
                (OrderId, Version, CustomerPohodaId, CustomerName, LoadingPlace, UnloadingPlace, LoadingDate, UnloadingDate,
                 FuelType, Liters, AgreedPrice, Surcharges, Note, CmrNumber, VehicleId, DriverId, TripId, Status, IsLocked,
                 PohodaOrderId, PohodaOrderNumber, PohodaInvoiceNumber, CreatedUtc,
                 PricingMode, SalePerKm, SalePerLiter, CostPerKm, RouteJson,
                 CancelReason, CustomerIco, CustomerDic, CustomerStreet, CustomerCity, CustomerZip, CustomerEmail, CustomerPhone)
            VALUES
                (@Id, @CurrentVersion, @CustomerPohodaId, @CustomerName, @LoadingPlace, @UnloadingPlace, @LoadingDate, @UnloadingDate,
                 @FuelType, @Liters, @AgreedPrice, @Surcharges, @Note, @CmrNumber, @VehicleId, @DriverId, @TripId, @Status, @IsLocked,
                 @PohodaOrderId, @PohodaOrderNumber, @PohodaInvoiceNumber, @UpdatedAt,
                 @PricingMode, @SalePerKm, @SalePerLiter, @CostPerKm, @RouteJson,
                 @CancelReason, @CustomerIco, @CustomerDic, @CustomerStreet, @CustomerCity, @CustomerZip, @CustomerEmail, @CustomerPhone)
            """,
            transaction,
            cancellationToken,
            new
            {
                order.Id,
                order.CurrentVersion,
                order.CustomerPohodaId,
                order.CustomerName,
                order.LoadingPlace,
                order.UnloadingPlace,
                order.LoadingDate,
                order.UnloadingDate,
                order.FuelType,
                order.Liters,
                order.AgreedPrice,
                order.Surcharges,
                order.Note,
                order.CmrNumber,
                order.VehicleId,
                order.DriverId,
                order.TripId,
                order.Status,
                order.IsLocked,
                order.PohodaOrderId,
                order.PohodaOrderNumber,
                order.PohodaInvoiceNumber,
                order.UpdatedAt,
                order.PricingMode,
                order.SalePerKm,
                order.SalePerLiter,
                order.CostPerKm,
                RouteJson = OrderRoute.Write(order),
                order.CancelReason,
                order.CustomerIco,
                order.CustomerDic,
                order.CustomerStreet,
                order.CustomerCity,
                order.CustomerZip,
                order.CustomerEmail,
                order.CustomerPhone
            }));

    private static Task<int> DeleteOrderAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string id,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            DELETE FROM dbo.Disp_SyncQueue WHERE OrderId = @Id;
            DELETE FROM dbo.Disp_OrderLeg WHERE OrderId = @Id;
            DELETE FROM dbo.Disp_OrderStop WHERE OrderId = @Id;
            DELETE FROM dbo.Disp_OrderVersion WHERE OrderId = @Id;
            DELETE FROM dbo.Disp_Order WHERE Id = @Id;
            """,
            transaction,
            cancellationToken,
            new { Id = id }));

    private static Task<int> DropOpenQueueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string id,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(Command(
            """
            UPDATE dbo.Disp_SyncQueue
            SET Status = N'Failed',
                LastError = N'Zakázka byla stornována před odesláním do Pohody.',
                ProcessedUtc = SYSDATETIMEOFFSET()
            WHERE OrderId = @Id AND Status IN (N'Pending', N'DryRun')
            """,
            transaction,
            cancellationToken,
            new { Id = id }));

    private static async Task ReplaceRouteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DispatchOrder order,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(Command(
            "DELETE FROM dbo.Disp_OrderLeg WHERE OrderId = @Id",
            transaction,
            cancellationToken,
            new { order.Id }));
        await connection.ExecuteAsync(Command(
            "DELETE FROM dbo.Disp_OrderStop WHERE OrderId = @Id",
            transaction,
            cancellationToken,
            new { order.Id }));
        foreach (var stop in order.Stops ?? [])
        {
            await connection.ExecuteAsync(Command(
                """
                INSERT INTO dbo.Disp_OrderStop (Id, OrderId, Sequence, Place, Liters, Note, ArrivalAt)
                VALUES (@Id, @OrderId, @Sequence, @Place, @Liters, @Note, @ArrivalAt)
                """,
                transaction,
                cancellationToken,
                new { stop.Id, OrderId = order.Id, stop.Sequence, stop.Place, stop.Liters, stop.Note, stop.ArrivalAt }));
        }

        foreach (var leg in order.Legs ?? [])
        {
            await connection.ExecuteAsync(Command(
                """
                INSERT INTO dbo.Disp_OrderLeg
                    (Id, OrderId, Sequence, FromPlace, ToPlace, Liters, Kilometers, SalePrice, SaleOverridden, CostPrice)
                VALUES
                    (@Id, @OrderId, @Sequence, @FromPlace, @ToPlace, @Liters, @Kilometers, @SalePrice, @SaleOverridden, @CostPrice)
                """,
                transaction,
                cancellationToken,
                new
                {
                    leg.Id,
                    OrderId = order.Id,
                    leg.Sequence,
                    leg.FromPlace,
                    leg.ToPlace,
                    leg.Liters,
                    leg.Kilometers,
                    leg.SalePrice,
                    leg.SaleOverridden,
                    leg.CostPrice
                }));
        }
    }

    private static async Task<IReadOnlyList<DispatchOrder>> AttachRoutesAsync(
        SqlConnection connection,
        IReadOnlyList<DispatchOrder> orders,
        CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
            return orders;

        var ids = orders.Select(order => order.Id).ToArray();
        var stops = (await connection.QueryAsync<RouteStopRow>(Command(
            """
            SELECT Id, OrderId, Sequence, Place, Liters, Note, ArrivalAt
            FROM dbo.Disp_OrderStop
            WHERE OrderId IN @Ids
            ORDER BY Sequence
            """,
            null,
            cancellationToken,
            new { Ids = ids }))).AsList();
        var legs = (await connection.QueryAsync<RouteLegRow>(Command(
            """
            SELECT Id, OrderId, Sequence, FromPlace, ToPlace, Liters, Kilometers, SalePrice, SaleOverridden, CostPrice
            FROM dbo.Disp_OrderLeg
            WHERE OrderId IN @Ids
            ORDER BY Sequence
            """,
            null,
            cancellationToken,
            new { Ids = ids }))).AsList();
        return orders.Select(order =>
        {
            var routed = order with
            {
                Stops = stops.Where(stop => stop.OrderId == order.Id).Select(stop => stop.ToStop()).ToArray(),
                Legs = legs.Where(leg => leg.OrderId == order.Id).Select(leg => leg.ToLeg()).ToArray()
            };
            return routed with { Quote = OrderPricing.Summarize(routed) };
        }).ToArray();
    }

    private static async Task<DispatchOrder> WithRouteAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        DispatchOrder order,
        CancellationToken cancellationToken)
    {
        var stops = (await connection.QueryAsync<OrderStop>(Command(
            """
            SELECT Id, Sequence, Place, Liters, Note, ArrivalAt
            FROM dbo.Disp_OrderStop
            WHERE OrderId = @Id
            ORDER BY Sequence
            """,
            transaction,
            cancellationToken,
            new { order.Id }))).AsList();
        var legs = (await connection.QueryAsync<OrderLeg>(Command(
            """
            SELECT Id, Sequence, FromPlace, ToPlace, Liters, Kilometers, SalePrice, SaleOverridden, CostPrice
            FROM dbo.Disp_OrderLeg
            WHERE OrderId = @Id
            ORDER BY Sequence
            """,
            transaction,
            cancellationToken,
            new { order.Id }))).AsList();
        var routed = order with { Stops = stops, Legs = legs };
        return routed with { Quote = OrderPricing.Summarize(routed) };
    }

    private static async Task UpsertQueueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DispatchOrder order,
        QueueUpsert queue,
        CancellationToken cancellationToken)
    {
        long? target = queue.TargetId;
        if (target is null)
        {
            target = await connection.ExecuteScalarAsync<long?>(Command(
                """
                SELECT TOP (1) Id
                FROM dbo.Disp_SyncQueue
                WHERE OrderId = @OrderId AND Status IN (N'Pending', N'DryRun')
                ORDER BY Id DESC
                """,
                transaction,
                cancellationToken,
                new { OrderId = order.Id }));
        }

        if (target is null)
        {
            await connection.ExecuteAsync(Command(
                """
                INSERT INTO dbo.Disp_SyncQueue
                    (OrderId, VersionFrom, VersionTo, Action, PayloadXml, Status, Attempts, LastError, CreatedUtc, ProcessedUtc, PohodaDocNumber, Warning)
                VALUES
                    (@OrderId, @VersionFrom, @VersionTo, @Action, @PayloadXml, @Status, 0, NULL, @CreatedUtc, NULL, NULL, @Warning)
                """,
                transaction,
                cancellationToken,
                new
                {
                    OrderId = order.Id,
                    queue.VersionFrom,
                    queue.VersionTo,
                    queue.Action,
                    queue.PayloadXml,
                    queue.Status,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Warning = Trim(queue.Warning, OrderSyncLimits.LastErrorMaxLength)
                }));
            return;
        }

        await connection.ExecuteAsync(Command(
            """
            UPDATE dbo.Disp_SyncQueue
            SET VersionFrom = @VersionFrom,
                VersionTo = @VersionTo,
                Action = @Action,
                PayloadXml = @PayloadXml,
                Status = @Status,
                Warning = @Warning,
                Attempts = CASE WHEN @Reset = 1 THEN 0 ELSE Attempts END,
                LastError = CASE WHEN @Reset = 1 THEN NULL ELSE LastError END,
                ProcessedUtc = CASE WHEN @Reset = 1 THEN NULL ELSE ProcessedUtc END
            WHERE Id = @Id
            """,
            transaction,
            cancellationToken,
            new
            {
                Id = target.Value,
                queue.VersionFrom,
                queue.VersionTo,
                queue.Action,
                queue.PayloadXml,
                queue.Status,
                Warning = Trim(queue.Warning, OrderSyncLimits.LastErrorMaxLength),
                Reset = queue.ResetAttempts ? 1 : 0
            }));
    }

    private const string QueueSql =
        """
        SELECT
            q.Id, q.OrderId, o.Code AS OrderCode, q.VersionFrom, q.VersionTo, q.Action, q.PayloadXml,
            q.Status, q.Attempts, q.LastError, q.CreatedUtc, q.ProcessedUtc, q.PohodaDocNumber, q.Warning
        FROM dbo.Disp_SyncQueue q
        INNER JOIN dbo.Disp_Order o ON o.Id = q.OrderId
        """;

    private static CommandDefinition Command(
        string sql,
        SqlTransaction? transaction,
        CancellationToken cancellationToken,
        object? parameters = null) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private static string? Trim(string? value, int max) => OrderSyncLimits.TrimToColumn(value, max);

    private sealed class RouteStopRow
    {
        public string Id { get; set; } = "";
        public string OrderId { get; set; } = "";
        public int Sequence { get; set; }
        public string Place { get; set; } = "";
        public decimal Liters { get; set; }
        public string? Note { get; set; }
        public DateTimeOffset? ArrivalAt { get; set; }

        public OrderStop ToStop() => new(Id, Sequence, Place, Liters, Note, ArrivalAt);
    }

    private sealed class RouteLegRow
    {
        public string Id { get; set; } = "";
        public string OrderId { get; set; } = "";
        public int Sequence { get; set; }
        public string FromPlace { get; set; } = "";
        public string ToPlace { get; set; } = "";
        public decimal Liters { get; set; }
        public decimal? Kilometers { get; set; }
        public decimal SalePrice { get; set; }
        public bool SaleOverridden { get; set; }
        public decimal CostPrice { get; set; }

        public OrderLeg ToLeg() => new(Id, Sequence, FromPlace, ToPlace, Liters, Kilometers, SalePrice, SaleOverridden, CostPrice);
    }

    private sealed class OrderRow
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public int? CustomerPohodaId { get; set; }
        public string CustomerName { get; set; } = "";
        public string LoadingPlace { get; set; } = "";
        public string UnloadingPlace { get; set; } = "";
        public DateOnly LoadingDate { get; set; }
        public DateOnly UnloadingDate { get; set; }
        public string FuelType { get; set; } = "";
        public decimal Liters { get; set; }
        public decimal AgreedPrice { get; set; }
        public decimal Surcharges { get; set; }
        public string? Note { get; set; }
        public string? CmrNumber { get; set; }
        public string? VehicleId { get; set; }
        public string? DriverId { get; set; }
        public string? TripId { get; set; }
        public string Status { get; set; } = "";
        public bool IsLocked { get; set; }
        public int CurrentVersion { get; set; }
        public int? PohodaOrderId { get; set; }
        public string? PohodaOrderNumber { get; set; }
        public string? PohodaInvoiceNumber { get; set; }
        public int? LastSentVersion { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? LatestSyncStatus { get; set; }
        public string? LatestSyncError { get; set; }
        public string? LatestSyncWarning { get; set; }
        public string PricingMode { get; set; } = OrderPricing.ByKm;
        public decimal SalePerKm { get; set; }
        public decimal SalePerLiter { get; set; }
        public decimal CostPerKm { get; set; }
        public string? CancelReason { get; set; }
        public string? CustomerIco { get; set; }
        public string? CustomerDic { get; set; }
        public string? CustomerStreet { get; set; }
        public string? CustomerCity { get; set; }
        public string? CustomerZip { get; set; }
        public string? CustomerEmail { get; set; }
        public string? CustomerPhone { get; set; }

        public DispatchOrder ToOrder() => new(
            Id, Code, CustomerPohodaId, CustomerName, LoadingPlace, UnloadingPlace, LoadingDate, UnloadingDate,
            FuelType, Liters, AgreedPrice, Surcharges, Note, CmrNumber, VehicleId, DriverId, TripId, Status,
            IsLocked, CurrentVersion, PohodaOrderId, PohodaOrderNumber, PohodaInvoiceNumber, LastSentVersion,
            CreatedAt, UpdatedAt, LatestSyncStatus, LatestSyncError, LatestSyncWarning, PricingMode, SalePerKm, SalePerLiter, CostPerKm,
            CancelReason: CancelReason, CustomerIco: CustomerIco, CustomerDic: CustomerDic, CustomerStreet: CustomerStreet,
            CustomerCity: CustomerCity, CustomerZip: CustomerZip, CustomerEmail: CustomerEmail, CustomerPhone: CustomerPhone);
    }

    private sealed class VehicleCostRow
    {
        public string Id { get; set; } = "";
        public decimal? CostPerKm { get; set; }
    }

    private sealed class VersionRow
    {
        public int Version { get; set; }
        public int? CustomerPohodaId { get; set; }
        public string CustomerName { get; set; } = "";
        public string LoadingPlace { get; set; } = "";
        public string UnloadingPlace { get; set; } = "";
        public DateOnly LoadingDate { get; set; }
        public DateOnly UnloadingDate { get; set; }
        public string FuelType { get; set; } = "";
        public decimal Liters { get; set; }
        public decimal AgreedPrice { get; set; }
        public decimal Surcharges { get; set; }
        public string? Note { get; set; }
        public string? CmrNumber { get; set; }
        public string? VehicleId { get; set; }
        public string? DriverId { get; set; }
        public string? TripId { get; set; }
        public string Status { get; set; } = "";
        public bool IsLocked { get; set; }
        public int? PohodaOrderId { get; set; }
        public string? PohodaOrderNumber { get; set; }
        public string? PohodaInvoiceNumber { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public string PricingMode { get; set; } = OrderPricing.ByKm;
        public decimal SalePerKm { get; set; }
        public decimal SalePerLiter { get; set; }
        public decimal CostPerKm { get; set; }
        public string? RouteJson { get; set; }
        public string? CancelReason { get; set; }
        public string? CustomerIco { get; set; }
        public string? CustomerDic { get; set; }
        public string? CustomerStreet { get; set; }
        public string? CustomerCity { get; set; }
        public string? CustomerZip { get; set; }
        public string? CustomerEmail { get; set; }
        public string? CustomerPhone { get; set; }

        public DispatchOrder ToOrder(DispatchOrder order)
        {
            var version = new DispatchOrder(
                order.Id, order.Code, CustomerPohodaId, CustomerName, LoadingPlace, UnloadingPlace, LoadingDate, UnloadingDate,
                FuelType, Liters, AgreedPrice, Surcharges, Note, CmrNumber, VehicleId, DriverId, TripId, Status,
                IsLocked, Version, PohodaOrderId, PohodaOrderNumber, PohodaInvoiceNumber, order.LastSentVersion,
                order.CreatedAt, CreatedUtc, null, null, null, PricingMode, SalePerKm, SalePerLiter, CostPerKm,
                CancelReason: CancelReason, CustomerIco: CustomerIco, CustomerDic: CustomerDic, CustomerStreet: CustomerStreet,
                CustomerCity: CustomerCity, CustomerZip: CustomerZip, CustomerEmail: CustomerEmail, CustomerPhone: CustomerPhone);
            return OrderRoute.Read(version, RouteJson);
        }
    }

    private sealed class QueueRow
    {
        public long Id { get; set; }
        public string OrderId { get; set; } = "";
        public string OrderCode { get; set; } = "";
        public int? VersionFrom { get; set; }
        public int VersionTo { get; set; }
        public string Action { get; set; } = "";
        public string PayloadXml { get; set; } = "";
        public string Status { get; set; } = "";
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset? ProcessedUtc { get; set; }
        public string? PohodaDocNumber { get; set; }
        public string? Warning { get; set; }

        public OrderSyncEntry ToEntry() => new(
            Id, OrderId, OrderCode, VersionFrom, VersionTo, Action, PayloadXml, Status, Attempts, LastError,
            CreatedUtc, ProcessedUtc, PohodaDocNumber, Warning);
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => DateOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
        };
    }
}
