using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace PHMTRANS.Pohoda;

public sealed record PohodaAddress(
    int Id,
    string Name,
    string? Company,
    string? Person,
    string? Street,
    string? City,
    string? Zip,
    string? Ico,
    string? Dic,
    string? Email,
    string? Phone);

public sealed class PohodaAddressBookException(string message, bool permissionDenied = false) : Exception(message)
{
    public bool PermissionDenied { get; } = permissionDenied;
}

public sealed record PohodaAddressGroup(int Id, string? Ico, string? Group, string? Name = null);

// Marže zákazníka v procentech z adresáře Pohody. Message vysvětlí, proč chybí.
public sealed record PohodaCustomerMargin(decimal? Percent, string? Message = null);

public interface IPohodaAddressBook
{
    Task<IReadOnlyList<PohodaAddress>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>Skupina adresy (dbo.AD.Skupina) podle ID nebo IČO.</summary>
    Task<IReadOnlyList<PohodaAddressGroup>> GroupsAsync(
        IReadOnlyCollection<int> ids,
        IReadOnlyCollection<string> icos,
        CancellationToken cancellationToken);

    /// <summary>Marže zákazníka v % (sloupec dbo.AD podle Pohoda:CustomerMarginColumn).</summary>
    Task<PohodaCustomerMargin> MarginAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(new PohodaCustomerMargin(null, PohodaAddressSql.MarginNotConfigured));
}

// Jen čtení. Do dbo.AD se nezapisuje.
// Adresář je ve firemní databázi Pohody, ne v dispečinku. Dotaz jde přes [StwPh_…].dbo.AD.
public static partial class PohodaAddressSql
{
    public const string DefaultDatabase = "StwPh_26950430_2026";

    public const string Login = "phmtrans_dispatch";

    public static string PermissionMessage(string database) =>
        $"Účet {Login} nemá oprávnění SELECT na adresář Pohody {database}.dbo.AD. " +
        $"Správce databáze musí spustit: GRANT SELECT ON [{database}].dbo.AD TO {Login};";

    public static string DatabaseName(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? DefaultDatabase : configured.Trim();
        if (!Identifier().IsMatch(name))
            throw new PohodaAddressBookException("Název databáze adresáře Pohody obsahuje nepovolené znaky.");
        return name;
    }

    public static string QualifiedTable(string database) => $"[{database}].dbo.AD";

    public static string SearchSql(string database) =>
        $"""
        SELECT TOP (20)
            ID, Firma, Jmeno, Ulice, Obec, PSC, ICO, DIC, Email, Tel
        FROM {QualifiedTable(database)}
        WHERE Firma LIKE @Like ESCAPE '!'
           OR Jmeno LIKE @Like ESCAPE '!'
           OR ICO LIKE @Like ESCAPE '!'
        ORDER BY Firma, Jmeno, ID
        """;

    public static string GroupsSql(string database) =>
        $"""
        SELECT ID, ICO, Skupina, Firma, Jmeno
        FROM {QualifiedTable(database)}
        WHERE ID IN @Ids OR ICO IN @Icos
        """;

    public const string MarginNotConfigured =
        "Marže zákazníka se z Pohody zatím nenačítá: není nastavený sloupec adresáře (Pohoda:CustomerMarginColumn).";

    public static string MarginColumn(string? configured)
    {
        var name = configured?.Trim() ?? "";
        if (name.Length == 0)
            return "";
        if (!Identifier().IsMatch(name))
            throw new PohodaAddressBookException("Název sloupce marže v adresáři Pohody obsahuje nepovolené znaky.");
        return name;
    }

    public static string MarginSql(string database, string column) =>
        $"SELECT TOP (1) TRY_CONVERT(decimal(18,4), [{column}]) FROM {QualifiedTable(database)} WHERE ID = @Id";

    public static string Like(string query)
    {
        var value = query.Trim();
        var escaped = value.Replace("!", "!!", StringComparison.Ordinal)
            .Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal)
            .Replace("[", "![", StringComparison.Ordinal);
        return "%" + escaped + "%";
    }

    public static bool IsPermissionError(int number) => number is 229 or 230 or 916;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex Identifier();
}

public sealed class PohodaAddressRow
{
    public int ID { get; set; }
    public string? Firma { get; set; }
    public string? Jmeno { get; set; }
    public string? Ulice { get; set; }
    public string? Obec { get; set; }
    public string? PSC { get; set; }
    public string? ICO { get; set; }
    public string? DIC { get; set; }
    public string? Email { get; set; }
    public string? Tel { get; set; }

    public PohodaAddress? ToAddress()
    {
        var company = Clean(Firma);
        var person = Clean(Jmeno);
        var name = company ?? person;
        if (ID < 1 || name is null)
            return null;

        return new PohodaAddress(
            ID,
            name,
            company,
            person,
            Clean(Ulice),
            Clean(Obec),
            Clean(PSC),
            Clean(ICO),
            Clean(DIC),
            Clean(Email),
            Clean(Tel));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class PohodaAddressGroupRow
{
    public int ID { get; set; }
    public string? ICO { get; set; }
    public string? Skupina { get; set; }
    public string? Firma { get; set; }
    public string? Jmeno { get; set; }

    public string? Name =>
        !string.IsNullOrWhiteSpace(Firma) ? Firma.Trim()
        : !string.IsNullOrWhiteSpace(Jmeno) ? Jmeno.Trim()
        : null;
}

public sealed class SqlPohodaAddressBook(IConfiguration configuration, IOptions<PohodaOptions> options) : IPohodaAddressBook
{
    private readonly string? _connectionString = configuration.GetConnectionString("Dispatch");

    public async Task<IReadOnlyList<PohodaAddress>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync<PohodaAddressRow>(
            PohodaAddressSql.SearchSql,
            new { Like = PohodaAddressSql.Like(query) },
            cancellationToken);
        return rows.Select(row => row.ToAddress()).Where(address => address is not null).Select(address => address!).ToArray();
    }

    public async Task<IReadOnlyList<PohodaAddressGroup>> GroupsAsync(
        IReadOnlyCollection<int> ids,
        IReadOnlyCollection<string> icos,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0 && icos.Count == 0)
            return [];

        var rows = await QueryAsync<PohodaAddressGroupRow>(
            PohodaAddressSql.GroupsSql,
            new { Ids = ids.ToArray(), Icos = icos.ToArray() },
            cancellationToken);
        return rows.Select(row => new PohodaAddressGroup(row.ID, row.ICO?.Trim(), row.Skupina?.Trim(), row.Name)).ToArray();
    }

    public async Task<PohodaCustomerMargin> MarginAsync(int id, CancellationToken cancellationToken)
    {
        var column = PohodaAddressSql.MarginColumn(options.Value.CustomerMarginColumn);
        if (column.Length == 0)
            return new PohodaCustomerMargin(null, PohodaAddressSql.MarginNotConfigured);

        var rows = await QueryAsync<decimal?>(database => PohodaAddressSql.MarginSql(database, column), new { Id = id }, cancellationToken);
        var percent = rows.FirstOrDefault();
        return percent is null
            ? new PohodaCustomerMargin(null, "Zákazník nemá v Pohodě vyplněnou marži.")
            : new PohodaCustomerMargin(percent);
    }

    private async Task<IEnumerable<T>> QueryAsync<T>(Func<string, string> sqlFor, object parameters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new PohodaAddressBookException(
                "Není nastavené ConnectionStrings:Dispatch, adresář Pohody (dbo.AD) nelze číst.");

        var database = PohodaAddressSql.DatabaseName(options.Value.AddressBookDatabase);
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            return await connection.QueryAsync<T>(
                new CommandDefinition(sqlFor(database), parameters, cancellationToken: cancellationToken));
        }
        catch (SqlException exception) when (PohodaAddressSql.IsPermissionError(exception.Number))
        {
            throw new PohodaAddressBookException(PohodaAddressSql.PermissionMessage(database), permissionDenied: true);
        }
        catch (SqlException exception)
        {
            throw new PohodaAddressBookException("Adresář Pohody (dbo.AD) se nepodařilo načíst. " + exception.Message);
        }
    }
}

