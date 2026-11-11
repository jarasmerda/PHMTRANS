using System.Text;
using Microsoft.AspNetCore.Http;
using PHMTRANS.Dispatch;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class OrderDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phmtrans-doc-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Files_go_to_the_order_folder_get_a_timestamp_and_never_overwrite()
    {
        var folder = new OrderDocumentFolder(_root);
        var at = DateTimeOffset.Parse("2026-11-06T08:30:15+01:00");

        var first = await folder.SaveAsync("Z2026-0001", "dodací list.JPG", new MemoryStream(Encoding.UTF8.GetBytes("a")), at, CancellationToken.None);
        var second = await folder.SaveAsync("Z2026-0001", "dodací list.jpg", new MemoryStream(Encoding.UTF8.GetBytes("bb")), at, CancellationToken.None);

        Assert.Equal("20261106-083015-dodací list.jpg", first);
        Assert.Equal("20261106-083015-dodací list-2.jpg", second);
        Assert.True(File.Exists(Path.Combine(_root, "Z2026-0001", first)));
        var list = folder.List("Z2026-0001");
        Assert.Equal(2, list.Count);
        Assert.All(list, doc => Assert.True(doc.IsImage));
        Assert.Empty(folder.List("Z2026-0002"));
        Assert.NotNull(folder.Find("Z2026-0001", first));
    }

    [Fact]
    public void Names_cannot_escape_the_folder_and_only_photos_and_pdf_are_allowed()
    {
        var folder = new OrderDocumentFolder(_root);
        Directory.CreateDirectory(Path.Combine(_root, "Z2026-0001"));
        File.WriteAllText(Path.Combine(_root, "tajne.pdf"), "x");

        Assert.Null(folder.Find("Z2026-0001", "../tajne.pdf"));
        Assert.Null(folder.Find("Z2026-0001", "..\tajne.pdf"));
        Assert.Null(folder.Find("Z2026-0001", "neni.pdf"));
        Assert.Equal("passwd.pdf", OrderDocumentRules.SafeName("../../etc/passwd.pdf"));
        Assert.Equal("a_b_c.png", OrderDocumentRules.SafeName("a:b?c.PNG"));
        Assert.Equal("doklad.pdf", OrderDocumentRules.SafeName("....pdf"));
        Assert.True(OrderDocumentRules.IsAllowed("foto.heic"));
        Assert.False(OrderDocumentRules.IsAllowed("virus.exe"));
        Assert.False(OrderDocumentRules.IsAllowed("stranka.html"));
        Assert.Equal("application/pdf", OrderDocumentRules.ContentType("cmr.PDF"));
    }

    [Fact]
    public void Upload_check_rejects_empty_large_and_unknown_files()
    {
        static IFormFile File(string name, long length) =>
            new FormFile(new MemoryStream(new byte[Math.Min(length, 4)]), 0, length, "files", name);

        Assert.NotNull(OrderDocumentEndpoints.Check([]));
        Assert.NotNull(OrderDocumentEndpoints.Check([File("a.exe", 10)]));
        Assert.NotNull(OrderDocumentEndpoints.Check([File("a.jpg", 0)]));
        Assert.NotNull(OrderDocumentEndpoints.Check([File("a.jpg", OrderDocumentRules.MaxFileBytes + 1)]));
        Assert.NotNull(OrderDocumentEndpoints.Check(Enumerable.Range(0, 11).Select(i => File($"{i}.jpg", 10)).ToArray()));
        Assert.Null(OrderDocumentEndpoints.Check([File("a.jpg", 10), File("cmr.pdf", 10)]));
    }

    [Fact]
    public void Order_detail_has_a_camera_button_and_documents_script()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "wwwroot")))
            root = Directory.GetParent(root)!.FullName;
        var html = System.IO.File.ReadAllText(Path.Combine(root, "wwwroot", "dispecink.html"));
        var js = System.IO.File.ReadAllText(Path.Combine(root, "wwwroot", "dispecink-doklady.js"));

        Assert.Contains("capture=\"environment\"", html, StringComparison.Ordinal);
        Assert.Contains("Vyfotit doklad", html, StringComparison.Ordinal);
        Assert.Contains("dispecink-doklady.js?v=4", html, StringComparison.Ordinal);
        Assert.Contains("window.loadOrderDocuments = load", js, StringComparison.Ordinal);
        Assert.Contains("/documents", js, StringComparison.Ordinal);
        Assert.Contains("id=\"doc-steps\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"doc-confirm\"", html, StringComparison.Ordinal);
        Assert.Contains("nakladka-nakladni-list", js, StringComparison.Ordinal);
        Assert.Contains("-staceci-list", js, StringComparison.Ordinal);
        Assert.Contains("-potvrzeni", js, StringComparison.Ordinal);
        Assert.Contains("#order-confirm", js, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nakladka-nakladni-list.jpg")]
    [InlineData("vykladka-1-staceci-list.jpg")]
    [InlineData("vykladka-12-potvrzeni.pdf")]
    public void Step_file_names_survive_the_safe_name(string name)
    {
        // Krok dokladu se v prohlížeči pozná podle názvu, server ho nesmí změnit.
        Assert.Equal(name, OrderDocumentRules.SafeName(name));
    }
}
