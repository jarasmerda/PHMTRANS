using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Cpq;

public static class CpqEndpoints
{
    public static void MapCpq(this WebApplication app)
    {
        app.MapGet("/api/config", (IConfiguration configuration) =>
            new ConfigResponse(configuration["Api:BaseUrl"] ?? ""));

        app.MapGet("/api/pohoda/status", async (PohodaMServerClient pohoda, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await pohoda.GetStatusAsync(cancellationToken));
            }
            catch (PohodaException exception)
            {
                return Problem(exception);
            }
        });

        for (var index = 1; index <= 7; index++)
        {
            var attribute = index;
            app.MapGet($"/CpqAttrValues{attribute}", (IWebHostEnvironment environment) =>
                Results.Text(string.Join('\n', ReadAttributeValues(environment, attribute)), "text/plain; charset=utf-8"));
        }

        app.MapGet("/CpqFilterValues", (HttpRequest request, IWebHostEnvironment environment) =>
        {
            var selected = ReadAttributes(request);
            var items = ReadCatalog(environment);
            var result = new Dictionary<string, string[]>();

            for (var index = 1; index <= 7; index++)
            {
                var matching = items.Where(item => selected.All(pair =>
                    pair.Key == index ||
                    item.TryGetValue(pair.Key.ToString(), out var value) &&
                    string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase)));

                result[index.ToString()] = matching
                    .Select(item => item.TryGetValue(index.ToString(), out var value) ? value : "")
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return Results.Json(result);
        });

        app.MapGet("/CpqItemPohodaCheck", async (
            HttpRequest request,
            PohodaMServerClient pohoda,
            IOptions<PohodaOptions> options,
            CancellationToken cancellationToken) =>
        {
            var attributes = ReadAttributes(request);
            if (attributes.Count == 0)
                return Results.BadRequest(new PohodaCheckResponse(false, "Chybí parametry attr1 až attr7."));

            var prefix = options.Value.CodePrefix;
            var code = attributes.Values.FirstOrDefault(value =>
                value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                value[prefix.Length..].All(char.IsDigit));
            var name = code is null
                ? string.Join(' ', attributes.OrderBy(pair => pair.Key).Select(pair => pair.Value))
                : null;

            try
            {
                var stocks = await pohoda.FindStockAsync(code, name, cancellationToken);
                var stock = stocks.FirstOrDefault();
                return stock is null
                    ? Results.Ok(new PohodaCheckResponse(false, "Zásoba v Pohodě není."))
                    : Results.Ok(new PohodaCheckResponse(true, "Zásoba v Pohodě existuje.", stock.Code));
            }
            catch (PohodaException exception)
            {
                return Problem(exception);
            }
        });

        app.MapGet("/CpqNextCode", async (
            PohodaMServerClient pohoda,
            IOptions<PohodaOptions> options,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var stocks = await pohoda.FindStockAsync(null, null, cancellationToken);
                return Results.Ok(new NextCodeResponse(NextCode(stocks, options.Value)));
            }
            catch (PohodaException exception)
            {
                return Problem(exception);
            }
        });

        app.MapPost("/CpqItemCreationPohoda", CreateStockAsync);
        app.MapPost("/api/pohoda/xml", CreateStockAsync);
    }

    private static async Task<IResult> CreateStockAsync(
        HttpRequest request,
        PohodaMServerClient pohoda,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
            return Results.BadRequest("Chybí XML karty zásoby.");

        try
        {
            var response = await pohoda.ForwardXmlAsync(buffer.ToArray(), cancellationToken);
            return Results.Content(response.Body, "text/xml; charset=utf-8", statusCode: response.StatusCode);
        }
        catch (PohodaException exception)
        {
            return Problem(exception);
        }
    }

    private static Dictionary<int, string> ReadAttributes(HttpRequest request)
    {
        var attributes = new Dictionary<int, string>();
        for (var index = 1; index <= 7; index++)
        {
            var value = request.Query[$"attr{index}"].ToString().Trim();
            if (value.Length > 0)
                attributes[index] = value;
        }

        return attributes;
    }

    private static IEnumerable<string> ReadAttributeValues(IWebHostEnvironment environment, int attribute)
    {
        var file = Path.Combine(environment.ContentRootPath, "Data", "cpq", $"attr{attribute}.txt");
        if (File.Exists(file))
        {
            return File.ReadLines(file)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0);
        }

        return ReadCatalog(environment)
            .Select(item => item.TryGetValue(attribute.ToString(), out var value) ? value.Trim() : "")
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Dictionary<string, string>> ReadCatalog(IWebHostEnvironment environment)
    {
        var file = Path.Combine(environment.ContentRootPath, "Data", "cpq", "items.json");
        if (!File.Exists(file))
            return [];

        var items = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(file));
        return items ?? [];
    }

    private static string NextCode(IReadOnlyList<PohodaStock> stocks, PohodaOptions options)
    {
        var pattern = new Regex($"^{Regex.Escape(options.CodePrefix)}(\\d+)$", RegexOptions.IgnoreCase);
        var highest = stocks
            .Select(stock => pattern.Match(stock.Code))
            .Where(match => match.Success)
            .Select(match => long.Parse(match.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();

        var width = Math.Max(options.CodeDigits, (highest + 1).ToString().Length);
        return $"{options.CodePrefix}{(highest + 1).ToString().PadLeft(width, '0')}";
    }

    private static IResult Problem(PohodaException exception) =>
        Results.Json(new PohodaCheckResponse(false, exception.Message), statusCode: StatusCodes.Status502BadGateway);
}
