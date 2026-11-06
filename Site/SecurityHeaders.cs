namespace PHMTRANS.Site;

public static class SecurityHeaders
{
    // Stránky načítají jen vlastní soubory. Inline skripty a styly ve stránkách zatím zůstávají, proto 'unsafe-inline'.
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static void UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                headers.Remove("X-Powered-By");

                // API vrací firemní data a stránky modulů se smí ukázat jen přihlášeným.
                // IIS/ARR ani prohlížeč je proto nesmí uložit, jinak by cache vracela stránku bez kontroly přihlášení.
                var contentType = context.Response.ContentType ?? "";
                if (context.Request.Path.StartsWithSegments("/api")
                    || contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    headers.CacheControl = "no-store, private";

                return Task.CompletedTask;
            });

            await next();
        });
    }
}
