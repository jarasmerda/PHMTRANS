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

                // Odpovědi API obsahují firemní data, prohlížeč ani proxy je nemají ukládat.
                if (context.Request.Path.StartsWithSegments("/api"))
                    headers.CacheControl = "no-store";

                return Task.CompletedTask;
            });

            await next();
        });
    }
}
