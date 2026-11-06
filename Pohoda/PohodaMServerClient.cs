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

    public async Task<PohodaXmlResponse> ImportXmlAsync(string xml, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Ico))
            throw new PohodaException("V nastavení chybí Pohoda:Ico.");

        var (username, password) = _options.ResolveWriteCredentials();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new PohodaException("V nastavení chybí přihlašovací údaje pro zápis do Pohody.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "xml");
        ApplyCommonHeaders(request);
        request.Headers.TryAddWithoutValidation("STW-Authorization", BasicHeader(username, password));
        var bytes = Windows1250.GetBytes(xml);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");

        using var response = await SendAsync(request, cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var body = Decode(responseBytes, response.Content.Headers.ContentType?.CharSet);
        if (!response.IsSuccessStatusCode)
            throw new PohodaException($"mServer vrátil {(int)response.StatusCode}: {Trim(body)}");

        return new PohodaXmlResponse((int)response.StatusCode, body);
    }

    private async Task<PohodaXmlResponse> ForwardXmlAsync(byte[] xml, CancellationToken cancellationToken)
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
        request.Headers.TryAddWithoutValidation("STW-Authorization", BasicHeader(_options.Username, _options.Password));
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

    private static string BasicHeader(string username, string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return $"Basic {token}";
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        var declared = XmlDeclaredEncoding(bytes);
        if (declared is not null && !IsUtf8Name(declared))
            return EncodingFor(declared).GetString(bytes);

        // XML se hlásí k UTF-8 a bajty jsou platné UTF-8: hlavička HTTP s windows-1250 je v tom případě chybná.
        if (declared is not null && IsValidUtf8(bytes))
            return Encoding.UTF8.GetString(bytes);

        if (IsWindows1250Name(charset) || !IsValidUtf8(bytes))
            return Windows1250.GetString(bytes);

        return Encoding.UTF8.GetString(bytes);
    }

    private static string? XmlDeclaredEncoding(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 240);
        var head = Encoding.ASCII.GetString(bytes, 0, length);
        const string marker = "encoding=";
        var start = head.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var index = start + marker.Length;
        while (index < head.Length && char.IsWhiteSpace(head[index]))
            index++;
        if (index >= head.Length || head[index] is not ('"' or '\''))
            return null;

        var quote = head[index];
        var end = head.IndexOf(quote, index + 1);
        if (end <= index + 1)
            return null;

        return head[(index + 1)..end].Trim();
    }

    private static bool IsUtf8Name(string name) =>
        name.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
        || name.Equals("utf8", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindows1250Name(string? name) =>
        name?.Contains("1250", StringComparison.OrdinalIgnoreCase) == true;

    private static Encoding EncodingFor(string name)
    {
        if (IsWindows1250Name(name))
            return Windows1250;

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return Windows1250;
        }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Trim(string value)
    {
        var compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 500 ? compact : compact[..500];
    }
}

public sealed record PohodaXmlResponse(int StatusCode, string Body);

public sealed class PohodaException(string message, Exception? inner = null) : Exception(message, inner);
