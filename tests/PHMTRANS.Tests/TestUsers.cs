using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace PHMTRANS.Tests;

public static class TestUsers
{
    public static ClaimsIdentity Identity(HttpRequest request, string scheme)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, request.Headers["X-Test-User"].ToString())
        };
        var modules = request.Headers["X-Test-Modules"].ToString();
        var selected = string.IsNullOrWhiteSpace(modules)
            ? new[] { "ERP", "Dispecink", "CRM" }
            : modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var module in selected)
            claims.Add(new Claim("module", module));
        if (string.Equals(request.Headers["X-Test-Admin"].ToString(), "true", StringComparison.OrdinalIgnoreCase))
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        return new ClaimsIdentity(claims, scheme);
    }
}
