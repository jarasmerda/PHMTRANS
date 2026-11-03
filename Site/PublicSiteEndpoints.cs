namespace PHMTRANS.Site;

public static class PublicSiteEndpoints
{
    public static void MapPublicSite(this WebApplication app)
    {
        // Veřejná prezentace. Kořen phmtrans.cz a www.phmtrans.cz nejde na login.
        app.Use(async (context, next) =>
        {
            var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim()
                ?? context.Request.Host.Host;
            var path = context.Request.Path.Value ?? "";

            if (IsPublicHost(host) && IsHome(path))
                context.Request.Path = "/landing.html";

            await next();
        });
    }

    private static bool IsPublicHost(string host)
    {
        var name = host.Split(':')[0];
        return name.Equals("phmtrans.cz", StringComparison.OrdinalIgnoreCase)
            || name.Equals("www.phmtrans.cz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHome(string path) =>
        path is "" or "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);
}
