namespace PHMTRANS.Site;

public static class PublicSiteEndpoints
{
    public static void MapPublicSite(this WebApplication app)
    {
        // Veřejná prezentace. Kořen každého hostitele kromě intranetu ukáže landing. www.phmtrans.cz přesměruje dřív,
        // kořen intranetu řeší ModuleGate.
        app.Use(async (context, next) =>
        {
            var host = SiteHost.Name(context);
            var path = context.Request.Path.Value ?? "";

            if (!SiteHost.IsIntranet(host) && IsHome(path))
                context.Request.Path = "/landing.html";

            await next();
        });
    }

    private static bool IsHome(string path) =>
        path is "" or "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);
}
