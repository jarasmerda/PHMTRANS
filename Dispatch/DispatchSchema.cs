using Microsoft.Data.SqlClient;

namespace PHMTRANS.Dispatch;

public static class DispatchSchema
{
    // Jen tabulky dispečinku. Skript nic nemaže a cizí tabulky nemění.
    // Dávky běží zvlášť: SQL Server kompiluje dávku dřív, než přidá sloupec.
    public static readonly string[] Batches = [Tables, Columns, Data];

    public static string Script => string.Join("\n", Batches);

    public const string Tables = """
        IF OBJECT_ID(N'dbo.Disp_Vehicle', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_Vehicle
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Disp_Vehicle PRIMARY KEY,
                Plate nvarchar(32) NOT NULL,
                Label nvarchar(160) NOT NULL,
                CostPerKm decimal(18,4) NULL
            );
        END;

        IF OBJECT_ID(N'dbo.Disp_VehicleCompartment', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_VehicleCompartment
            (
                VehicleId nvarchar(64) NOT NULL,
                Number int NOT NULL,
                CapacityLiters decimal(18,3) NOT NULL,
                CONSTRAINT PK_Disp_VehicleCompartment PRIMARY KEY (VehicleId, Number),
                CONSTRAINT FK_Disp_VehicleCompartment_Vehicle FOREIGN KEY (VehicleId) REFERENCES dbo.Disp_Vehicle (Id),
                CONSTRAINT CK_Disp_VehicleCompartment_Number CHECK (Number BETWEEN 1 AND 10),
                CONSTRAINT CK_Disp_VehicleCompartment_Capacity CHECK (CapacityLiters > 0)
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

        IF OBJECT_ID(N'dbo.Disp_FuelType', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_FuelType
            (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Disp_FuelType PRIMARY KEY,
                Name nvarchar(80) NOT NULL
            );
            CREATE UNIQUE INDEX UX_Disp_FuelType_Name ON dbo.Disp_FuelType (Name);
        END;

        IF OBJECT_ID(N'dbo.Disp_LoadingPlace', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_LoadingPlace
            (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Disp_LoadingPlace PRIMARY KEY,
                Name nvarchar(160) NOT NULL,
                Active bit NOT NULL CONSTRAINT DF_Disp_LoadingPlace_Active DEFAULT 1
            );
            CREATE UNIQUE INDEX UX_Disp_LoadingPlace_Name ON dbo.Disp_LoadingPlace (Name);
        END;

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
                PricingMode nvarchar(10) NOT NULL CONSTRAINT DF_Disp_Order_PricingMode DEFAULT N'Km',
                SalePerKm decimal(18,4) NOT NULL CONSTRAINT DF_Disp_Order_SalePerKm DEFAULT 0,
                SalePerLiter decimal(18,4) NOT NULL CONSTRAINT DF_Disp_Order_SalePerLiter DEFAULT 0,
                CostPerKm decimal(18,4) NOT NULL CONSTRAINT DF_Disp_Order_CostPerKm DEFAULT 0,
                CancelReason nvarchar(500) NULL,
                CustomerIco nvarchar(15) NULL,
                CustomerDic nvarchar(18) NULL,
                CustomerStreet nvarchar(64) NULL,
                CustomerCity nvarchar(45) NULL,
                CustomerZip nvarchar(15) NULL,
                CustomerEmail nvarchar(98) NULL,
                CustomerPhone nvarchar(40) NULL,
                FuelTypeId int NULL,
                CONSTRAINT FK_Disp_Order_Vehicle FOREIGN KEY (VehicleId) REFERENCES dbo.Disp_Vehicle (Id),
                CONSTRAINT FK_Disp_Order_FuelType FOREIGN KEY (FuelTypeId) REFERENCES dbo.Disp_FuelType (Id),
                CONSTRAINT FK_Disp_Order_Driver FOREIGN KEY (DriverId) REFERENCES dbo.Disp_Driver (Id),
                CONSTRAINT FK_Disp_Order_Trip FOREIGN KEY (TripId) REFERENCES dbo.Disp_Trip (Id)
            );
            CREATE UNIQUE INDEX UX_Disp_Order_Code ON dbo.Disp_Order (Code);
            CREATE INDEX IX_Disp_Order_Trip ON dbo.Disp_Order (TripId);
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
                PricingMode nvarchar(10) NOT NULL CONSTRAINT DF_Disp_OrderVersion_PricingMode DEFAULT N'Km',
                SalePerKm decimal(18,4) NOT NULL CONSTRAINT DF_Disp_OrderVersion_SalePerKm DEFAULT 0,
                SalePerLiter decimal(18,4) NOT NULL CONSTRAINT DF_Disp_OrderVersion_SalePerLiter DEFAULT 0,
                CostPerKm decimal(18,4) NOT NULL CONSTRAINT DF_Disp_OrderVersion_CostPerKm DEFAULT 0,
                CancelReason nvarchar(500) NULL,
                CustomerIco nvarchar(15) NULL,
                CustomerDic nvarchar(18) NULL,
                CustomerStreet nvarchar(64) NULL,
                CustomerCity nvarchar(45) NULL,
                CustomerZip nvarchar(15) NULL,
                CustomerEmail nvarchar(98) NULL,
                CustomerPhone nvarchar(40) NULL,
                FuelTypeId int NULL,
                RouteJson nvarchar(max) NULL,
                CONSTRAINT FK_Disp_OrderVersion_FuelType FOREIGN KEY (FuelTypeId) REFERENCES dbo.Disp_FuelType (Id),
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

        IF OBJECT_ID(N'dbo.Disp_OrderStop', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_OrderStop
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Disp_OrderStop PRIMARY KEY,
                OrderId nvarchar(64) NOT NULL,
                Sequence int NOT NULL,
                Place nvarchar(160) NOT NULL,
                Liters decimal(18,3) NOT NULL,
                Note nvarchar(500) NULL,
                ArrivalAt datetimeoffset(3) NULL,
                FuelTypeId int NULL,
                CONSTRAINT FK_Disp_OrderStop_Order FOREIGN KEY (OrderId) REFERENCES dbo.Disp_Order (Id),
                CONSTRAINT FK_Disp_OrderStop_FuelType FOREIGN KEY (FuelTypeId) REFERENCES dbo.Disp_FuelType (Id)
            );
            CREATE UNIQUE INDEX UX_Disp_OrderStop_OrderSeq ON dbo.Disp_OrderStop (OrderId, Sequence);
        END;

        IF OBJECT_ID(N'dbo.Disp_OrderLeg', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_OrderLeg
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Disp_OrderLeg PRIMARY KEY,
                OrderId nvarchar(64) NOT NULL,
                Sequence int NOT NULL,
                FromPlace nvarchar(160) NOT NULL,
                ToPlace nvarchar(160) NOT NULL,
                Liters decimal(18,3) NOT NULL,
                Kilometers decimal(18,3) NULL,
                SalePrice decimal(18,2) NOT NULL,
                SaleOverridden bit NOT NULL,
                CostPrice decimal(18,2) NOT NULL,
                KmSource nvarchar(16) NULL,
                CONSTRAINT FK_Disp_OrderLeg_Order FOREIGN KEY (OrderId) REFERENCES dbo.Disp_Order (Id)
            );
            CREATE UNIQUE INDEX UX_Disp_OrderLeg_OrderSeq ON dbo.Disp_OrderLeg (OrderId, Sequence);
        END;

        IF OBJECT_ID(N'dbo.Disp_OrderLoad', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_OrderLoad
            (
                OrderId nvarchar(64) NOT NULL,
                Sequence int NOT NULL,
                FuelTypeId int NULL,
                FuelType nvarchar(80) NOT NULL,
                Liters decimal(18,3) NOT NULL,
                CONSTRAINT PK_Disp_OrderLoad PRIMARY KEY (OrderId, Sequence),
                CONSTRAINT FK_Disp_OrderLoad_Order FOREIGN KEY (OrderId) REFERENCES dbo.Disp_Order (Id),
                CONSTRAINT FK_Disp_OrderLoad_FuelType FOREIGN KEY (FuelTypeId) REFERENCES dbo.Disp_FuelType (Id)
            );
        END;

        IF OBJECT_ID(N'dbo.Disp_Setting', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_Setting
            (
                [Key] nvarchar(64) NOT NULL CONSTRAINT PK_Disp_Setting PRIMARY KEY,
                Value nvarchar(200) NOT NULL
            );
        END;

        IF OBJECT_ID(N'dbo.Disp_Distance', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Disp_Distance
            (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Disp_Distance PRIMARY KEY,
                FromPlace nvarchar(160) NOT NULL,
                ToPlace nvarchar(160) NOT NULL,
                Km decimal(18,3) NOT NULL,
                UpdatedAt datetimeoffset(3) NOT NULL,
                UpdatedBy nvarchar(160) NULL
            );
            CREATE UNIQUE INDEX UX_Disp_Distance_Pair ON dbo.Disp_Distance (FromPlace, ToPlace);
        END;
        """;

    public const string Columns = """
        IF COL_LENGTH(N'dbo.Disp_Trip', N'VehicleId') IS NULL
            ALTER TABLE dbo.Disp_Trip ADD VehicleId nvarchar(64) NULL;

        IF COL_LENGTH(N'dbo.Disp_Trip', N'DriverId') IS NULL
            ALTER TABLE dbo.Disp_Trip ADD DriverId nvarchar(64) NULL;

        IF COL_LENGTH(N'dbo.Disp_SyncQueue', N'Warning') IS NULL
            ALTER TABLE dbo.Disp_SyncQueue ADD Warning nvarchar(1000) NULL;

        IF COL_LENGTH(N'dbo.Disp_Vehicle', N'CostPerKm') IS NULL
            ALTER TABLE dbo.Disp_Vehicle ADD CostPerKm decimal(18,4) NULL;

        IF COL_LENGTH(N'dbo.Disp_Order', N'PricingMode') IS NULL
            ALTER TABLE dbo.Disp_Order ADD PricingMode nvarchar(10) NOT NULL CONSTRAINT DF_Disp_Order_PricingMode DEFAULT N'Km';

        IF COL_LENGTH(N'dbo.Disp_Order', N'SalePerKm') IS NULL
            ALTER TABLE dbo.Disp_Order ADD SalePerKm decimal(18,4) NOT NULL CONSTRAINT DF_Disp_Order_SalePerKm DEFAULT 0;

        IF COL_LENGTH(N'dbo.Disp_Order', N'SalePerLiter') IS NULL
            ALTER TABLE dbo.Disp_Order ADD SalePerLiter decimal(18,4) NOT NULL CONSTRAINT DF_Disp_Order_SalePerLiter DEFAULT 0;

        IF COL_LENGTH(N'dbo.Disp_Order', N'CostPerKm') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CostPerKm decimal(18,4) NOT NULL CONSTRAINT DF_Disp_Order_CostPerKm DEFAULT 0;

        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'PricingMode') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD PricingMode nvarchar(10) NOT NULL CONSTRAINT DF_Disp_OrderVersion_PricingMode DEFAULT N'Km';

        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'SalePerKm') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD SalePerKm decimal(18,4) NOT NULL CONSTRAINT DF_Disp_OrderVersion_SalePerKm DEFAULT 0;

        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'SalePerLiter') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD SalePerLiter decimal(18,4) NOT NULL CONSTRAINT DF_Disp_OrderVersion_SalePerLiter DEFAULT 0;

        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CostPerKm') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CostPerKm decimal(18,4) NOT NULL CONSTRAINT DF_Disp_OrderVersion_CostPerKm DEFAULT 0;

        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'RouteJson') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD RouteJson nvarchar(max) NULL;

        IF COL_LENGTH(N'dbo.Disp_Order', N'CancelReason') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CancelReason nvarchar(500) NULL;
        IF COL_LENGTH(N'dbo.Disp_Order', N'CustomerIco') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CustomerIco nvarchar(15) NULL;
        IF COL_LENGTH(N'dbo.Disp_Order', N'CustomerDic') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CustomerDic nvarchar(18) NULL;
        IF COL_LENGTH(N'dbo.Disp_Order', N'CustomerStreet') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CustomerStreet nvarchar(64) NULL;
        IF COL_LENGTH(N'dbo.Disp_Order', N'CustomerCity') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CustomerCity nvarchar(45) NULL;
        IF COL_LENGTH(N'dbo.Disp_Order', N'CustomerZip') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CustomerZip nvarchar(15) NULL;
        IF COL_LENGTH(N'dbo.Disp_Order', N'CustomerEmail') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CustomerEmail nvarchar(98) NULL;
        IF COL_LENGTH(N'dbo.Disp_Order', N'CustomerPhone') IS NULL
            ALTER TABLE dbo.Disp_Order ADD CustomerPhone nvarchar(40) NULL;

        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CancelReason') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CancelReason nvarchar(500) NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CustomerIco') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CustomerIco nvarchar(15) NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CustomerDic') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CustomerDic nvarchar(18) NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CustomerStreet') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CustomerStreet nvarchar(64) NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CustomerCity') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CustomerCity nvarchar(45) NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CustomerZip') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CustomerZip nvarchar(15) NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CustomerEmail') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CustomerEmail nvarchar(98) NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CustomerPhone') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD CustomerPhone nvarchar(40) NULL;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = N'IX_Disp_Order_Trip' AND object_id = OBJECT_ID(N'dbo.Disp_Order'))
            CREATE INDEX IX_Disp_Order_Trip ON dbo.Disp_Order (TripId);

        IF COL_LENGTH(N'dbo.Disp_Order', N'FuelTypeId') IS NULL
            ALTER TABLE dbo.Disp_Order ADD FuelTypeId int NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderStop', N'FuelTypeId') IS NULL
            ALTER TABLE dbo.Disp_OrderStop ADD FuelTypeId int NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'FuelTypeId') IS NULL
            ALTER TABLE dbo.Disp_OrderVersion ADD FuelTypeId int NULL;
        IF COL_LENGTH(N'dbo.Disp_OrderLeg', N'KmSource') IS NULL
            ALTER TABLE dbo.Disp_OrderLeg ADD KmSource nvarchar(16) NULL;

        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Disp_Order_FuelType')
            ALTER TABLE dbo.Disp_Order ADD CONSTRAINT FK_Disp_Order_FuelType FOREIGN KEY (FuelTypeId) REFERENCES dbo.Disp_FuelType (Id);
        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Disp_OrderStop_FuelType')
            ALTER TABLE dbo.Disp_OrderStop ADD CONSTRAINT FK_Disp_OrderStop_FuelType FOREIGN KEY (FuelTypeId) REFERENCES dbo.Disp_FuelType (Id);
        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Disp_OrderVersion_FuelType')
            ALTER TABLE dbo.Disp_OrderVersion ADD CONSTRAINT FK_Disp_OrderVersion_FuelType FOREIGN KEY (FuelTypeId) REFERENCES dbo.Disp_FuelType (Id);
        """;

    // Textový OrderCode jen doplní chybějící TripId. Řádky, které už jízdu mají, se nemění.
    // Stará zakázka bez vykládek dostane jednu zastávku a jeden úsek s původní prodejní cenou.
    public const string Data = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        IF NOT EXISTS (SELECT 1 FROM dbo.Disp_Setting WHERE [Key] = N'CostPerKm')
            INSERT INTO dbo.Disp_Setting ([Key], Value) VALUES (N'CostPerKm', N'0');

        UPDATE o
        SET TripId = t.Id
        FROM dbo.Disp_Order o
        INNER JOIN dbo.Disp_Trip t ON t.OrderCode = o.Code
        WHERE o.TripId IS NULL
          AND t.OrderCode IS NOT NULL
          AND LTRIM(RTRIM(t.OrderCode)) <> N''
          AND 1 = (
              SELECT COUNT(*)
              FROM dbo.Disp_Trip t2
              WHERE t2.OrderCode = o.Code
                AND t2.OrderCode IS NOT NULL
                AND LTRIM(RTRIM(t2.OrderCode)) <> N''
          );

        INSERT INTO dbo.Disp_OrderStop (Id, OrderId, Sequence, Place, Liters, Note, ArrivalAt)
        SELECT
            LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')),
            o.Id,
            1,
            o.UnloadingPlace,
            o.Liters,
            NULL,
            NULL
        FROM dbo.Disp_Order o
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Disp_OrderStop s WHERE s.OrderId = o.Id);

        INSERT INTO dbo.Disp_OrderLeg
            (Id, OrderId, Sequence, FromPlace, ToPlace, Liters, Kilometers, SalePrice, SaleOverridden, CostPrice)
        SELECT
            LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')),
            o.Id,
            1,
            o.LoadingPlace,
            o.UnloadingPlace,
            o.Liters,
            NULL,
            o.AgreedPrice,
            1,
            0
        FROM dbo.Disp_Order o
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Disp_OrderLeg l WHERE l.OrderId = o.Id);

        UPDATE v
        SET RouteJson = (
            SELECT
                N'Km' AS pricingMode,
                CAST(0 AS decimal(18,4)) AS salePerKm,
                CAST(0 AS decimal(18,4)) AS salePerLiter,
                CAST(0 AS decimal(18,4)) AS costPerKm,
                JSON_QUERY((
                    SELECT
                        LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')) AS id,
                        1 AS sequence,
                        v.UnloadingPlace AS place,
                        v.Liters AS liters
                    FOR JSON PATH
                )) AS stops,
                JSON_QUERY((
                    SELECT
                        LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')) AS id,
                        1 AS sequence,
                        v.LoadingPlace AS fromPlace,
                        v.UnloadingPlace AS toPlace,
                        v.Liters AS liters,
                        v.AgreedPrice AS salePrice,
                        CAST(1 AS bit) AS saleOverridden,
                        CAST(0 AS decimal(18,2)) AS costPrice
                    FOR JSON PATH
                )) AS legs
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        )
        FROM dbo.Disp_OrderVersion v
        WHERE v.RouteJson IS NULL;

        IF NOT EXISTS (SELECT 1 FROM dbo.Disp_FuelType WHERE Name = N'Motorová nafta')
            INSERT INTO dbo.Disp_FuelType (Name) VALUES (N'Motorová nafta');
        IF NOT EXISTS (SELECT 1 FROM dbo.Disp_FuelType WHERE Name = N'Benzín')
            INSERT INTO dbo.Disp_FuelType (Name) VALUES (N'Benzín');

        UPDATE o
        SET FuelTypeId = f.Id
        FROM dbo.Disp_Order o
        INNER JOIN dbo.Disp_FuelType f ON LOWER(LTRIM(RTRIM(o.FuelType))) = LOWER(LTRIM(RTRIM(f.Name)))
        WHERE o.FuelTypeId IS NULL;

        UPDATE v
        SET FuelTypeId = f.Id
        FROM dbo.Disp_OrderVersion v
        INNER JOIN dbo.Disp_FuelType f ON LOWER(LTRIM(RTRIM(v.FuelType))) = LOWER(LTRIM(RTRIM(f.Name)))
        WHERE v.FuelTypeId IS NULL;

        UPDATE s
        SET FuelTypeId = o.FuelTypeId
        FROM dbo.Disp_OrderStop s
        INNER JOIN dbo.Disp_Order o ON o.Id = s.OrderId
        WHERE s.FuelTypeId IS NULL
          AND o.FuelTypeId IS NOT NULL;

        COMMIT TRANSACTION;
        """;

    public static async Task EnsureAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var batch in Batches)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
