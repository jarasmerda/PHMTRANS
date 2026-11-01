using Microsoft.Data.SqlClient;

namespace PHMTRANS.Crm;

public static class CrmSchema
{
    // Jen tabulky CRM. Skript nic nemaže a cizí tabulky nemění.
    public const string Script = """
        IF OBJECT_ID(N'dbo.Crm_Customer', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Crm_Customer
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Crm_Customer PRIMARY KEY,
                Code nvarchar(20) NOT NULL,
                Kind nvarchar(20) NOT NULL,
                Name nvarchar(160) NOT NULL,
                Ico nvarchar(16) NULL,
                Email nvarchar(160) NULL,
                Phone nvarchar(40) NULL,
                Street nvarchar(160) NULL,
                City nvarchar(80) NULL,
                Zip nvarchar(16) NULL,
                Country nvarchar(80) NULL,
                Note nvarchar(500) NULL,
                UpdatedAt datetimeoffset(3) NOT NULL
            );
            CREATE UNIQUE INDEX UX_Crm_Customer_Code ON dbo.Crm_Customer (Code);
        END;

        IF OBJECT_ID(N'dbo.Crm_Contact', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Crm_Contact
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Crm_Contact PRIMARY KEY,
                CustomerId nvarchar(64) NOT NULL,
                Name nvarchar(160) NOT NULL,
                Role nvarchar(80) NULL,
                Email nvarchar(160) NULL,
                Phone nvarchar(40) NULL,
                Note nvarchar(500) NULL,
                UpdatedAt datetimeoffset(3) NOT NULL,
                CONSTRAINT FK_Crm_Contact_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Crm_Customer (Id)
            );
        END;

        IF OBJECT_ID(N'dbo.Crm_Opportunity', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Crm_Opportunity
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Crm_Opportunity PRIMARY KEY,
                Code nvarchar(20) NOT NULL,
                CustomerId nvarchar(64) NOT NULL,
                Title nvarchar(160) NOT NULL,
                Stage nvarchar(20) NOT NULL,
                Amount decimal(18, 2) NULL,
                ExpectedClose date NULL,
                Note nvarchar(500) NULL,
                UpdatedAt datetimeoffset(3) NOT NULL,
                CONSTRAINT FK_Crm_Opportunity_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Crm_Customer (Id)
            );
            CREATE UNIQUE INDEX UX_Crm_Opportunity_Code ON dbo.Crm_Opportunity (Code);
        END;

        IF OBJECT_ID(N'dbo.Crm_Activity', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Crm_Activity
            (
                Id nvarchar(64) NOT NULL CONSTRAINT PK_Crm_Activity PRIMARY KEY,
                Code nvarchar(20) NOT NULL,
                Kind nvarchar(20) NOT NULL,
                CustomerId nvarchar(64) NULL,
                OpportunityId nvarchar(64) NULL,
                Subject nvarchar(160) NOT NULL,
                Body nvarchar(1000) NULL,
                OccurredAt datetimeoffset(3) NOT NULL,
                UpdatedAt datetimeoffset(3) NOT NULL,
                CONSTRAINT FK_Crm_Activity_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Crm_Customer (Id),
                CONSTRAINT FK_Crm_Activity_Opportunity FOREIGN KEY (OpportunityId) REFERENCES dbo.Crm_Opportunity (Id),
                CONSTRAINT CK_Crm_Activity_Target CHECK (CustomerId IS NOT NULL OR OpportunityId IS NOT NULL)
            );
            CREATE UNIQUE INDEX UX_Crm_Activity_Code ON dbo.Crm_Activity (Code);
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
