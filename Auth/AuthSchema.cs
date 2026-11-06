using Microsoft.Data.SqlClient;

namespace PHMTRANS.Auth;

public static class AuthSchema
{
    // Jen tabulky účtů. Skript nic nemaže a cizí tabulky nemění.
    public const string Script = """
        IF OBJECT_ID(N'dbo.Auth_User', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Auth_User
            (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_Auth_User PRIMARY KEY,
                Username nvarchar(80) NOT NULL,
                DisplayName nvarchar(160) NOT NULL,
                PasswordHash nvarchar(500) NOT NULL,
                IsAdmin bit NOT NULL,
                IsActive bit NOT NULL,
                AllowErp bit NOT NULL,
                AllowDispatch bit NOT NULL,
                AllowCrm bit NOT NULL,
                CreatedAt datetimeoffset(3) NOT NULL,
                LastLoginAt datetimeoffset(3) NULL,
                FailedAttempts int NOT NULL CONSTRAINT DF_Auth_User_FailedAttempts DEFAULT (0),
                LockoutUntil datetimeoffset(3) NULL
            );
            CREATE UNIQUE INDEX UX_Auth_User_Username ON dbo.Auth_User (Username);
        END;

        -- Povolené reporty ERP čárkami. NULL = všechny (účty z doby před výběrem reportů).
        IF COL_LENGTH(N'dbo.Auth_User', N'ErpReports') IS NULL
            ALTER TABLE dbo.Auth_User ADD ErpReports nvarchar(200) NULL;
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
