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
