namespace PHMTRANS.Site;

public static class PublicSiteEndpoints
{
    public static void MapPublicSite(this WebApplication app)
    {
        // Veřejná prezentace. Kořen phmtrans.cz nejde na login. www.phmtrans.cz přesměruje dřív.
        app.Use(async (context, next) =>
        {
            var host = SiteHost.Name(context);
            var path = context.Request.Path.Value ?? "";

            if (SiteHost.IsApex(host) && IsHome(path))
                context.Request.Path = "/landing.html";

            await next();
        });
    }

    private static bool IsHome(string path) =>
        path is "" or "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);
}
