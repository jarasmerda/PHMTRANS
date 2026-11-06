using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace PHMTRANS.Dispatch;

// Doklady k zakázce (fotky, PDF). Ukládají se do složky Dispatch:DocumentsPath,
// do podsložky podle kódu zakázky. Složku synchronizuje OneDrive na serveru.
public static class OrderDocumentRules
{
    public const string PathKey = "Dispatch:DocumentsPath";
    public const long MaxFileBytes = 25L * 1024 * 1024;
    public const int MaxFilesPerUpload = 10;
    public const long MaxRequestBytes = MaxFileBytes * MaxFilesPerUpload;

    public const string NotConfiguredMessage =
        "Složka pro doklady není nastavená (Dispatch:DocumentsPath). Doklady zatím nejde ukládat.";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".pdf"] = "application/pdf"
    };

    public static bool IsAllowed(string fileName) => Types.ContainsKey(Path.GetExtension(fileName));

    public static string ContentType(string fileName) =>
        Types.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    public static bool IsImage(string fileName) => ContentType(fileName).StartsWith("image/", StringComparison.Ordinal);

    // Název souboru bez cesty a bez znaků, které Windows ani OneDrive nesnesou.
    public static string SafeName(string? original)
    {
        var name = (original ?? "").Replace('\\', '/').Split('/')[^1].Trim();
        // Bez Path.GetExtension: na Windows by „a:b.png“ bral jako disk a:.
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 || (dot == 0 && name.Length > 1) ? name[dot..].ToLowerInvariant() : "";
        var stem = dot >= 0 ? name[..dot] : name;
        var builder = new StringBuilder();
        foreach (var c in stem.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(c) || "<>:\"/\\|?*#%".Contains(c))
                builder.Append('_');
            else
                builder.Append(c);
        }
        var clean = builder.ToString().Trim(' ', '.', '_');
        if (clean.Length == 0)
            clean = "doklad";
        if (clean.Length > 60)
            clean = clean[..60].TrimEnd(' ', '.');
        return clean + extension;
    }

    public static string FolderName(string orderCode) => SafeName(orderCode + ".x")[..^2];
}

public sealed record OrderDocument(string Name, long Size, DateTimeOffset ModifiedAt, bool IsImage);

public sealed class OrderDocumentFolder(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public IReadOnlyList<OrderDocument> List(string orderCode)
    {
        var folder = FolderOf(orderCode);
        if (!Directory.Exists(folder))
            return [];
        return new DirectoryInfo(folder)
            .EnumerateFiles()
            .Where(file => OrderDocumentRules.IsAllowed(file.Name) && !file.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => new OrderDocument(
                file.Name,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                OrderDocumentRules.IsImage(file.Name)))
            .ToArray();
    }

    public async Task<string> SaveAsync(string orderCode, string originalName, Stream content, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var folder = FolderOf(orderCode);
        Directory.CreateDirectory(folder);
        var safe = OrderDocumentRules.SafeName(originalName);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var stem = $"{stamp}-{Path.GetFileNameWithoutExtension(safe)}";
        var extension = Path.GetExtension(safe);
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? stem + extension : $"{stem}-{attempt}{extension}";
            var path = Path.Combine(folder, name);
            try
            {
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await content.CopyToAsync(target, cancellationToken);
                return name;
            }
            catch (IOException) when (File.Exists(path) && attempt < 100)
            {
                // Stejný název ve stejnou sekundu, zkusí se další pořadové číslo.
            }
        }
    }

    // Cesta k souboru, jen když název nevede mimo složku zakázky.
    public string? Find(string orderCode, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains("..", StringComparison.Ordinal))
            return null;
        if (!OrderDocumentRules.IsAllowed(name))
            return null;
        var folder = FolderOf(orderCode);
        var path = Path.GetFullPath(Path.Combine(folder, name));
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(path) ? path : null;
    }

    private string FolderOf(string orderCode) =>
        Path.GetFullPath(Path.Combine(Root, OrderDocumentRules.FolderName(orderCode)));
}

public static class OrderDocumentEndpoints
{
    public static void MapOrderDocuments(this WebApplication app)
    {
        var api = app.MapGroup("/api/dispatch");
        api.MapGet("/orders/{id}/documents", ListDocuments);
        api.MapPost("/orders/{id}/documents", UploadDocuments)
            .WithMetadata(new RequestSizeLimitAttribute(OrderDocumentRules.MaxRequestBytes))
            .WithFormOptions(multipartBodyLengthLimit: OrderDocumentRules.MaxRequestBytes);
        api.MapGet("/orders/{id}/documents/{name}", GetDocument);
    }

    public static OrderDocumentFolder? Folder(IConfiguration configuration)
    {
        var path = configuration[OrderDocumentRules.PathKey];
        return string.IsNullOrWhiteSpace(path) ? null : new OrderDocumentFolder(path.Trim());
    }

    private static async Task<IResult> ListDocuments(string id, IOrderStore store, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var folder = Folder(configuration);
        if (folder is null)
            return Results.Ok(new { configured = false, message = OrderDocumentRules.NotConfiguredMessage, documents = Array.Empty<OrderDocument>() });
        return await WithOrder(id, store, cancellationToken, order =>
            Task.FromResult(Results.Ok(new { configured = true, message = (string?)null, documents = folder.List(order.Code) })));
    }

    private static async Task<IResult> UploadDocuments(
        string id,
        HttpRequest request,
        IOrderStore store,
        IConfiguration configuration,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var folder = Folder(configuration);
        if (folder is null)
            return Results.BadRequest(new { message = OrderDocumentRules.NotConfiguredMessage });
        if (!request.HasFormContentType)
            return Results.BadRequest(new { message = "Pošlete soubory jako formulář." });

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException)
        {
            return Results.BadRequest(new { message = $"Soubory jsou příliš velké. Najednou nejvýš {OrderDocumentRules.MaxFilesPerUpload} souborů po {OrderDocumentRules.MaxFileBytes / 1024 / 1024} MB." });
        }

        var files = form.Files;
        var error = Check(files);
        if (error is not null)
            return Results.BadRequest(new { message = error });

        return await WithOrder(id, store, cancellationToken, async order =>
        {
            var saved = new List<string>();
            try
            {
                foreach (var file in files)
                {
                    await using var stream = file.OpenReadStream();
                    saved.Add(await folder.SaveAsync(order.Code, file.FileName, stream, DateTimeOffset.Now, cancellationToken));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                loggers.CreateLogger("OrderDocuments").LogError(exception, "Doklad k zakázce {Code} se nepodařilo uložit do {Root}.", order.Code, folder.Root);
                return Results.Json(
                    new { message = "Doklad se nepodařilo uložit do složky na serveru. Zkontrolujte, že služba má do složky právo zápisu.", saved },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(new { saved, documents = folder.List(order.Code) });
        });
    }

    public static string? Check(IReadOnlyList<IFormFile> files)
    {
        if (files.Count == 0)
            return "Vyberte soubor nebo vyfoťte doklad.";
        if (files.Count > OrderDocumentRules.MaxFilesPerUpload)
            return $"Najednou jde přiložit nejvýš {OrderDocumentRules.MaxFilesPerUpload} souborů.";
        foreach (var file in files)
        {
            if (!OrderDocumentRules.IsAllowed(file.FileName))
                return $"Soubor {file.FileName} nejde přiložit. Povolené jsou fotky (JPG, PNG, WEBP, HEIC) a PDF.";
            if (file.Length <= 0)
                return $"Soubor {file.FileName} je prázdný.";
            if (file.Length > OrderDocumentRules.MaxFileBytes)
                return $"Soubor {file.FileName} je větší než {OrderDocumentRules.MaxFileBytes / 1024 / 1024} MB.";
        }
        return null;
    }

    private static async Task<IResult> GetDocument(string id, string name, IOrderStore store, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var folder = Folder(configuration);
        if (folder is null)
            return Results.NotFound(new { message = OrderDocumentRules.NotConfiguredMessage });
        return await WithOrder(id, store, cancellationToken, order =>
        {
            var path = folder.Find(order.Code, name);
            return Task.FromResult(path is null
                ? Results.NotFound(new { message = "Doklad neexistuje." })
                : Results.File(path, OrderDocumentRules.ContentType(name), enableRangeProcessing: true));
        });
    }

    private static async Task<IResult> WithOrder(
        string id,
        IOrderStore store,
        CancellationToken cancellationToken,
        Func<DispatchOrder, Task<IResult>> action)
    {
        try
        {
            var bundle = await store.GetAsync(id, cancellationToken);
            return bundle is null
                ? Results.NotFound(new { message = "Zakázka neexistuje." })
                : await action(bundle.Order);
        }
        catch (OrderStoreException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
