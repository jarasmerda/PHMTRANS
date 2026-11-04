using Microsoft.Data.SqlClient;

namespace PHMTRANS.Dispatch;

public static class DispatchSchema
{
    // Jen tabulky dispečinku. Skript nic nemaže a cizí tabulky nemění.
    public const string Script = """
        IF OBJECT_ID(N'dbo.Disp_Vehicle', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_Vehicle
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Disp_Vehicle PRIMARY KEY,
                Plate nvarchar(32) NOT NULL,
                Label nvarchar(160) NOT NULL
            );
        END;

        IF OBJECT_ID(N'dbo.Disp_Driver', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_Driver
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Disp_Driver PRIMARY KEY,
                Name nvarchar(160) NOT NULL,
                Phone nvarchar(40) NULL
            );
        END;

        IF OBJECT_ID(N'dbo.Disp_Trip', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_Trip
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Disp_Trip PRIMARY KEY,
                Code nvarchar(20) NOT NULL,
                OrderCode nvarchar(40) NULL,
                Customer nvarchar(160) NOT NULL,
                LoadingPlace nvarchar(160) NOT NULL,
                UnloadingPlace nvarchar(160) NOT NULL,
                Cargo nvarchar(80) NOT NULL,
                PlannedStart datetimeoffset(3) NOT NULL,
                Status nvarchar(20) NOT NULL,
                VehicleId nvarchar(64) NULL,
                DriverId nvarchar(64) NULL,
                Note nvarchar(500) NULL,
                UpdatedAt datetimeoffset(3) NOT NULL,
                CONSTRAINT FK_Disp_Trip_Vehicle FOREIGN KEY (VehicleId) REFERENCES dbo.Disp_Vehicle (Id),
                CONSTRAINT FK_Disp_Trip_Driver FOREIGN KEY (DriverId) REFERENCES dbo.Disp_Driver (Id)
            );
            CREATE UNIQUE INDEX UX_Disp_Trip_Code ON dbo.Disp_Trip (Code);
        END;

        -- Doplnění sloupců na už existující Disp_Trip. Řádky ani cizí tabulky se nemění.
        IF COL_LENGTH(N'dbo.Disp_Trip', N'VehicleId') IS NULL
            ALTER TABLE dbo.Disp_Trip ADD VehicleId nvarchar(64) NULL;

        IF COL_LENGTH(N'dbo.Disp_Trip', N'DriverId') IS NULL
            ALTER TABLE dbo.Disp_Trip ADD DriverId nvarchar(64) NULL;

        IF OBJECT_ID(N'dbo.Disp_Order', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_Order
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Disp_Order PRIMARY KEY,
                Code nvarchar(20) NOT NULL,
                CustomerPohodaId int NULL,
                CustomerName nvarchar(160) NOT NULL,
                LoadingPlace nvarchar(160) NOT NULL,
                UnloadingPlace nvarchar(160) NOT NULL,
                LoadingDate date NOT NULL,
                UnloadingDate date NOT NULL,
                FuelType nvarchar(80) NOT NULL,
                Liters decimal(18,3) NOT NULL,
                AgreedPrice decimal(18,2) NOT NULL,
                Surcharges decimal(18,2) NOT NULL,
                Note nvarchar(500) NULL,
                CmrNumber nvarchar(40) NULL,
                VehicleId nvarchar(64) NULL,
                DriverId nvarchar(64) NULL,
                TripId nvarchar(64) NULL,
                Status nvarchar(30) NOT NULL,
                IsLocked bit NOT NULL,
                CurrentVersion int NOT NULL,
                PohodaOrderId int NULL,
                PohodaOrderNumber nvarchar(40) NULL,
                PohodaInvoiceNumber nvarchar(40) NULL,
                LastSentVersion int NULL,
                CreatedAt datetimeoffset(3) NOT NULL,
                UpdatedAt datetimeoffset(3) NOT NULL,
                CONSTRAINT FK_Disp_Order_Vehicle FOREIGN KEY (VehicleId) REFERENCES dbo.Disp_Vehicle (Id),
                CONSTRAINT FK_Disp_Order_Driver FOREIGN KEY (DriverId) REFERENCES dbo.Disp_Driver (Id),
                CONSTRAINT FK_Disp_Order_Trip FOREIGN KEY (TripId) REFERENCES dbo.Disp_Trip (Id)
            );
            CREATE UNIQUE INDEX UX_Disp_Order_Code ON dbo.Disp_Order (Code);
        END;

        IF OBJECT_ID(N'dbo.Disp_OrderVersion', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_OrderVersion
            (
                OrderId nvarchar(64) NOT NULL,
                Version int NOT NULL,
                CustomerPohodaId int NULL,
                CustomerName nvarchar(160) NOT NULL,
                LoadingPlace nvarchar(160) NOT NULL,
                UnloadingPlace nvarchar(160) NOT NULL,
                LoadingDate date NOT NULL,
                UnloadingDate date NOT NULL,
                FuelType nvarchar(80) NOT NULL,
                Liters decimal(18,3) NOT NULL,
                AgreedPrice decimal(18,2) NOT NULL,
                Surcharges decimal(18,2) NOT NULL,
                Note nvarchar(500) NULL,
                CmrNumber nvarchar(40) NULL,
                VehicleId nvarchar(64) NULL,
                DriverId nvarchar(64) NULL,
                TripId nvarchar(64) NULL,
                Status nvarchar(30) NOT NULL,
                IsLocked bit NOT NULL,
                PohodaOrderId int NULL,
                PohodaOrderNumber nvarchar(40) NULL,
                PohodaInvoiceNumber nvarchar(40) NULL,
                CreatedUtc datetimeoffset(3) NOT NULL,
                CONSTRAINT PK_Disp_OrderVersion PRIMARY KEY (OrderId, Version),
                CONSTRAINT FK_Disp_OrderVersion_Order FOREIGN KEY (OrderId) REFERENCES dbo.Disp_Order (Id)
            );
        END;

        IF OBJECT_ID(N'dbo.Disp_SyncQueue', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_SyncQueue
            (
                Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Disp_SyncQueue PRIMARY KEY,
                OrderId nvarchar(64) NOT NULL,
                VersionFrom int NULL,
                VersionTo int NOT NULL,
                Action nvarchar(20) NOT NULL,
                PayloadXml nvarchar(max) NOT NULL,
                Status nvarchar(20) NOT NULL,
                Attempts int NOT NULL,
                LastError nvarchar(1000) NULL,
                CreatedUtc datetimeoffset(3) NOT NULL,
                ProcessedUtc datetimeoffset(3) NULL,
                PohodaDocNumber nvarchar(40) NULL,
                Warning nvarchar(1000) NULL,
                CONSTRAINT FK_Disp_SyncQueue_Order FOREIGN KEY (OrderId) REFERENCES dbo.Disp_Order (Id)
            );
            CREATE INDEX IX_Disp_SyncQueue_Status ON dbo.Disp_SyncQueue (Status, Id);
            CREATE INDEX IX_Disp_SyncQueue_Order ON dbo.Disp_SyncQueue (OrderId, Id);
        END;

        IF COL_LENGTH(N'dbo.Disp_SyncQueue', N'Warning') IS NULL
            ALTER TABLE dbo.Disp_SyncQueue ADD Warning nvarchar(1000) NULL;
        """;

    public static async Task EnsureAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Script;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
