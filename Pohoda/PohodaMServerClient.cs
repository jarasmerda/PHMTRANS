using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace PHMTRANS.Pohoda;

public sealed class PohodaMServerClient(
    HttpClient http,
    IOptions<PohodaOptions> options,
    ILogger<PohodaMServerClient> logger)
{
    private static readonly Encoding Windows1250 = Encoding.GetEncoding(1250);

    private readonly PohodaOptions _options = options.Value;

    public async Task<PohodaStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var body = await GetAsync("status", cancellationToken);
        return ParseStatus(body);
    }

    public async Task<IReadOnlyList<PohodaStock>> FindStockAsync(
        string? code,
        string? name,
        CancellationToken cancellationToken)
    {
        EnsureUser();
        if (string.IsNullOrWhiteSpace(_options.Ico))
            throw new PohodaException("V nastavení chybí Pohoda:Ico.");

        var xml = BuildListStockXml(code, name);
        var body = await PostXmlAsync(Windows1250.GetBytes(xml), cancellationToken);
        return ReadStocks(body);
    }

    public async Task<IReadOnlyList<IssuedInvoice>> ListIssuedInvoicesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        EnsureUser();
        if (string.IsNullOrWhiteSpace(_options.Ico))
            throw new PohodaException("V nastavení chybí Pohoda:Ico.");

        var xml = IssuedInvoiceExport.CreateRequest(_options, from, to);
        var body = await PostXmlAsync(Windows1250.GetBytes(xml), cancellationToken);
        return IssuedInvoiceExport.Read(body);
    }

    public async Task<PohodaXmlResponse> ForwardXmlAsync(byte[] xml, CancellationToken cancellationToken)
    {
        EnsureUser();
        if (xml.Length == 0)
            throw new PohodaException("Chybí XML karty zásoby.");

        using var request = CreateXmlRequest(xml);
        using var response = await SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var body = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
        return new PohodaXmlResponse((int)response.StatusCode, body);
    }

    private async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        ApplyCommonHeaders(request);
        using var response = await SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var body = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
        if (!response.IsSuccessStatusCode)
            throw new PohodaException($"mServer vrátil {(int)response.StatusCode}: {Trim(body)}");

        return body;
    }

    private async Task<string> PostXmlAsync(byte[] xml, CancellationToken cancellationToken)
    {
        var result = await ForwardXmlAsync(xml, cancellationToken);
        if (result.StatusCode is < 200 or >= 300)
            throw new PohodaException($"mServer vrátil {result.StatusCode}: {Trim(result.Body)}");

        return result.Body;
    }

    private HttpRequestMessage CreateXmlRequest(byte[] xml)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "xml");
        ApplyCommonHeaders(request);
        request.Headers.TryAddWithoutValidation("STW-Authorization", AuthorizationHeader());
        request.Content = new ByteArrayContent(xml);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return request;
    }

    private void ApplyCommonHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "STORMWARE HTTP client. http://www.stormware.cz");
        request.Headers.TryAddWithoutValidation("Accept", "application/xml");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        request.Headers.TryAddWithoutValidation("Host", _options.BaseUrl.TrimEnd('/'));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Spojení s Pohoda mServer na {BaseUrl} selhalo.", _options.BaseUrl);
            throw new PohodaException($"mServer na {_options.BaseUrl} neodpovídá.", ex);
        }
    }

    private void EnsureUser()
    {
        if (string.IsNullOrWhiteSpace(_options.Username))
            throw new PohodaException("V nastavení chybí Pohoda:Username.");
    }

    private string AuthorizationHeader()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
        return $"Basic {token}";
    }

    private string BuildListStockXml(string? code, string? name)
    {
        XNamespace dat = "http://www.stormware.cz/schema/version_2/data.xsd";
        XNamespace lStk = "http://www.stormware.cz/schema/version_2/list_stock.xsd";
        XNamespace ftr = "http://www.stormware.cz/schema/version_2/filter.xsd";
        XNamespace stk = "http://www.stormware.cz/schema/version_2/stock.xsd";
        XNamespace typ = "http://www.stormware.cz/schema/version_2/type.xsd";

        var filter = new XElement(ftr + "filter");
        if (!string.IsNullOrWhiteSpace(code))
            filter.Add(new XElement(ftr + "code", code));
        if (!string.IsNullOrWhiteSpace(name))
            filter.Add(new XElement(ftr + "name", name));

        var document = new XElement(dat + "dataPack",
            new XAttribute("version", "2.0"),
            new XAttribute("id", "phmtrans"),
            new XAttribute("ico", _options.Ico),
            new XAttribute("application", _options.Application),
            new XAttribute("note", "PHMTRANS"),
            new XAttribute(XNamespace.Xmlns + "dat", dat),
            new XAttribute(XNamespace.Xmlns + "lStk", lStk),
            new XAttribute(XNamespace.Xmlns + "ftr", ftr),
            new XAttribute(XNamespace.Xmlns + "stk", stk),
            new XAttribute(XNamespace.Xmlns + "typ", typ),
            new XElement(dat + "dataPackItem",
                new XAttribute("version", "2.0"),
                new XAttribute("id", "001"),
                new XElement(lStk + "listStockRequest",
                    new XAttribute("version", "2.0"),
                    new XAttribute("stockVersion", "2.0"),
                    new XElement(lStk + "requestStock", filter))));

        return $"<?xml version=\"1.0\" encoding=\"Windows-1250\"?>{document.ToString(SaveOptions.DisableFormatting)}";
    }

    private static PohodaStatusResponse ParseStatus(string body)
    {
        try
        {
            var document = XDocument.Parse(body);
            var status = Value(document, "status");
            var processing = int.TryParse(Value(document, "processing"), out var count) ? count : 0;
            return new PohodaStatusResponse(true, status, Value(document, "message"), Value(document, "server"), processing);
        }
        catch (System.Xml.XmlException)
        {
            return new PohodaStatusResponse(false, "", Trim(body), "", 0);
        }
    }

    private static IReadOnlyList<PohodaStock> ReadStocks(string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new PohodaException("mServer nevrátil platné XML.", ex);
        }

        var state = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "responsePack")
            ?.Attribute("state")?.Value;

        if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase))
        {
            var note = Value(document, "note");
            throw new PohodaException(string.IsNullOrWhiteSpace(note) ? "Pohoda vrátila chybu." : note);
        }

        return document.Descendants()
            .Where(element => element.Name.LocalName == "stockHeader")
            .Select(header => new PohodaStock(
                header.Elements().FirstOrDefault(element => element.Name.LocalName == "code")?.Value.Trim() ?? "",
                header.Elements().FirstOrDefault(element => element.Name.LocalName == "name")?.Value.Trim() ?? ""))
            .Where(stock => stock.Code.Length > 0)
            .ToArray();
    }

    private static string Value(XDocument document, string localName) =>
        document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim() ?? "";

    private static string Decode(byte[] bytes, string? charset)
    {
        var encoding = charset?.Contains("1250", StringComparison.OrdinalIgnoreCase) == true
            ? Windows1250
            : Encoding.UTF8;
        return encoding.GetString(bytes);
    }

    private static string Trim(string value)
    {
        var compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 500 ? compact : compact[..500];
    }
}

public sealed record PohodaStock(string Code, string Name);

public sealed record PohodaXmlResponse(int StatusCode, string Body);

public sealed record PohodaStatusResponse(bool Ok, string Status, string Message, string Server, int Processing);

public sealed class PohodaException(string message, Exception? inner = null) : Exception(message, inner);
