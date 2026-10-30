using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PHMTRANS.Pohoda;

namespace PHMTRANS.Bi;

public static class ErpAuthEndpoints
{
    public static void MapErpAuth(this WebApplication app)
    {
        app.MapPost("/api/login", async (LoginRequest request, PohodaMServerClient pohoda, HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                if (!await pohoda.CredentialsAreValidAsync(request.Username, request.Password, cancellationToken))
                    return Results.Json(new { message = "Neplatné přihlašovací údaje." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            catch (PohodaException exception)
            {
                return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, request.Username.Trim())],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));
            return Results.Ok(new { username = request.Username.Trim() });
        });

        app.MapPost("/api/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });
    }
}

public sealed record LoginRequest(string Username, string Password);
