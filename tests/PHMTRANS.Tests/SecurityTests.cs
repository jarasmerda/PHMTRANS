using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using PHMTRANS.Auth;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class SecurityTests : IClassFixture<PublicSiteApp>
{
    private readonly PublicSiteApp _app;

    public SecurityTests(PublicSiteApp app) => _app = app;

    [Theory]
    [InlineData("/api/pohoda/status")]
    [InlineData("/api/pohoda/xml")]
    [InlineData("/api/config")]
    [InlineData("/api/neexistuje")]
    public async Task Unknown_api_requires_login(string path)
    {
        using var client = Client();
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/weatherforecast")]
    [InlineData("/vlastovka")]
    [InlineData("/CpqNextCode")]
    [InlineData("/CpqItemPohodaCheck?attr1=PE00001")]
    public async Task Removed_endpoints_are_gone(string path)
    {
        using var client = Client();
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Removed_pohoda_proxy_does_not_accept_post()
    {
        using var client = Client();
        var response = await client.PostAsync("/CpqItemCreationPohoda", new StringContent("<xml/>"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var client = Client();
        var page = await client.GetAsync("/landing.html");
        var api = await client.GetAsync("/api/auth/me");

        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.True(api.Headers.CacheControl?.NoStore);
        Assert.True(page.Headers.CacheControl?.NoStore);
        Assert.True(page.Headers.CacheControl?.Private);
    }

    [Fact]
    public void Rate_limiter_forgets_idle_addresses()
    {
        var limiter = new LoginRateLimiter();
        var start = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("198.51.100.1", 1, start));
        Assert.False(limiter.TryAcquire("198.51.100.1", 1, start.AddSeconds(10)));
        Assert.True(limiter.TryAcquire("198.51.100.1", 1, start.AddMinutes(2)));
    }

    private HttpClient Client() =>
        _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
