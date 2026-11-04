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

public interface IPohodaAddressBook
{
    Task<IReadOnlyList<PohodaAddress>> SearchAsync(string query, CancellationToken cancellationToken);
}

// Jen čtení. Do dbo.AD se nezapisuje.
public static partial class PohodaAddressSql
{
    public const string DefaultDatabase = "Smerco_26950430_2026";

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

public sealed class SqlPohodaAddressBook(IConfiguration configuration, IOptions<PohodaOptions> options) : IPohodaAddressBook
{
    private readonly string? _connectionString = configuration.GetConnectionString("Dispatch");

    public async Task<IReadOnlyList<PohodaAddress>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new PohodaAddressBookException(
                "Dispečink nemá nastavené ConnectionStrings:Dispatch, adresář Pohody (dbo.AD) nelze číst.");

        var database = PohodaAddressSql.DatabaseName(options.Value.AddressBookDatabase);
        var sql = PohodaAddressSql.SearchSql(database);
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            var rows = await connection.QueryAsync<PohodaAddressRow>(
                new CommandDefinition(sql, new { Like = PohodaAddressSql.Like(query) }, cancellationToken: cancellationToken));
            return rows.Select(row => row.ToAddress()).Where(address => address is not null).Select(address => address!).ToArray();
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

public sealed class PreviewPohodaAddressBook : IPohodaAddressBook
{
    private static readonly PohodaAddress[] Samples =
    [
        new(42, "Velkoobchod Demo s.r.o.", "Velkoobchod Demo s.r.o.", null, "Skladová 12", "Cerekvice nad Loučnou", "569 53", "12345678", "CZ12345678", "info@demo.cz", "+420 601 000 001"),
        new(43, "Agropodnik Demo a.s.", "Agropodnik Demo a.s.", "Jan Novák", "Polní 4", "Lovosice", "410 02", "87654321", "CZ87654321", "agro@demo.cz", "+420 602 000 002")
    ];

    public Task<IReadOnlyList<PohodaAddress>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var term = query.Trim();
        IReadOnlyList<PohodaAddress> found = Samples
            .Where(item =>
                item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (item.Ico?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (item.Person?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(20)
            .ToArray();
        return Task.FromResult(found);
    }
}
