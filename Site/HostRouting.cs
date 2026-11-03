namespace PHMTRANS.Site;

public static class SiteHost
{
    public const string IntranetOrigin = "https://intranet.phmtrans.cz";

    public static string Name(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
        var raw = string.IsNullOrWhiteSpace(forwarded)
            ? context.Request.Host.Host
            : forwarded.Split(',')[0].Trim();
        var colon = raw.LastIndexOf(':');
        if (colon > 0 && raw.IndexOf(':') == colon && raw[(colon + 1)..].All(char.IsDigit))
            raw = raw[..colon];

        return raw;
    }

    public static bool IsWwwPublic(string host) =>
        host.Equals("www.phmtrans.cz", StringComparison.OrdinalIgnoreCase);

    public static bool IsApex(string host) =>
        host.Equals("phmtrans.cz", StringComparison.OrdinalIgnoreCase);

    public static bool IsIntranet(string host) =>
        host.Equals("intranet.phmtrans.cz", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.intranet.phmtrans.cz", StringComparison.OrdinalIgnoreCase);

    public static string? LegacyModuleRoot(string host)
    {
        if (Is(host, "erp.phmtrans.cz") || Is(host, "www.erp.phmtrans.cz"))
            return "/ERP";
        if (Is(host, "disp.phmtrans.cz") || Is(host, "www.disp.phmtrans.cz"))
            return "/Dispecink";
        if (Is(host, "crm.phmtrans.cz") || Is(host, "www.crm.phmtrans.cz"))
            return "/CRM";
        return null;
    }

    public static bool IsHealth(PathString path) =>
        path.Equals("/health", StringComparison.OrdinalIgnoreCase);

    public static string LegacyTarget(string moduleRoot, string path)
    {
        if (path is "" or "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            return moduleRoot;

        if (moduleRoot == "/ERP" && path.Equals("/erp.html", StringComparison.OrdinalIgnoreCase))
            return moduleRoot;
        if (moduleRoot == "/Dispecink" && path.Equals("/dispecink.html", StringComparison.OrdinalIgnoreCase))
            return moduleRoot;
        if (moduleRoot == "/CRM" && path.Equals("/crm.html", StringComparison.OrdinalIgnoreCase))
            return moduleRoot;

        return moduleRoot + path;
    }

    private static bool Is(string host, string expected) =>
        host.Equals(expected, StringComparison.OrdinalIgnoreCase);
}

public static class ModulePath
{
    public static bool TryRewrite(PathString path, out PathString rewritten)
    {
        rewritten = path;
        var value = path.Value ?? "";
        if (!TrySplit(value, out var module, out var rest))
            return false;

        var canonical = Canonical(module);
        if (rest is "" or "/"
            || rest.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
            || rest.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            rewritten = canonical;
            return true;
        }

        rewritten = new PathString(rest);
        return true;
    }

    private static bool TrySplit(string path, out string module, out string rest)
    {
        module = "";
        rest = "";
        var trimmed = path.TrimStart('/');
        if (trimmed.Length == 0)
            return false;

        var slash = trimmed.IndexOf('/');
        var head = slash < 0 ? trimmed : trimmed[..slash];
        rest = slash < 0 ? "" : trimmed[slash..];
        module = head.ToLowerInvariant() switch
        {
            "erp" => "erp",
            "dispecink" => "dispecink",
            "crm" => "crm",
            "admin" => "admin",
            "heslo" => "heslo",
            "vyber" => "vyber",
            _ => ""
        };
        return module.Length > 0;
    }

    private static string Canonical(string module) => module switch
    {
        "erp" => "/erp.html",
        "dispecink" => "/dispecink.html",
        "crm" => "/crm.html",
        "admin" => "/admin.html",
        "heslo" => "/heslo.html",
        "vyber" => "/vyber.html",
        _ => "/"
    };
}

public static class HostRouting
{
    public static void UseHostRouting(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (SiteHost.IsHealth(context.Request.Path))
            {
                await next();
                return;
            }

            var host = SiteHost.Name(context);
            var path = context.Request.Path.Value ?? "/";
            var query = context.Request.QueryString.Value ?? "";

            if (SiteHost.IsWwwPublic(host))
            {
                context.Response.Redirect("https://phmtrans.cz" + (path.Length == 0 ? "/" : path) + query, permanent: true);
                return;
            }

            if (SiteHost.LegacyModuleRoot(host) is { } moduleRoot)
            {
                var target = SiteHost.LegacyTarget(moduleRoot, path);
                context.Response.Redirect(SiteHost.IntranetOrigin + target + query, permanent: true);
                return;
            }

            if (ModulePath.TryRewrite(context.Request.Path, out var rewritten))
            {
                if (IsModulePage(rewritten))
                    context.Items["IntranetPublicPath"] = (context.Request.Path.Value ?? "/") + query;
                context.Request.Path = rewritten;
            }

            await next();
        });
    }

    private static bool IsModulePage(PathString path) =>
        path.Equals("/erp.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/dispecink.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/crm.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/admin.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/heslo.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/vyber.html", StringComparison.OrdinalIgnoreCase);
}
