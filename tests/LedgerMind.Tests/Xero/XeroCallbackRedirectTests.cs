using System.Net;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LedgerMind.Tests.Xero;

// Xero:ReturnUrl set ho to callback JSON ki jagah browser ko wapis app par bhejta hai
public sealed class XeroReturnUrlFactory : TestWebApplicationFactory
{
    public const string ReturnUrl = "http://localhost:5173/integrations";

    public XeroReturnUrlFactory()
    {
        Settings["Xero:ReturnUrl"] = ReturnUrl;
    }
}

// ReturnUrl me pehle se query aur #fragment
public sealed class XeroReturnUrlWithQueryFactory : TestWebApplicationFactory
{
    public const string ReturnUrl = "http://localhost:5173/app?tab=integrations#xero";

    public XeroReturnUrlWithQueryFactory()
    {
        Settings["Xero:ReturnUrl"] = ReturnUrl;
    }
}

public class XeroCallbackRedirectTests : IClassFixture<XeroReturnUrlFactory>
{
    private readonly XeroReturnUrlFactory _factory;

    public XeroCallbackRedirectTests(XeroReturnUrlFactory factory)
    {
        _factory = factory;
    }

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Successful_callback_redirects_with_xero_connected()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var tenantId = XeroTestHelpers.NewTenantId();
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("redirect-access", "redirect-refresh"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.ConnectionsJson(tenantId, "Redirect Co"));

        var response = await NoRedirectClient()
            .GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.Redirect);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(XeroReturnUrlFactory.ReturnUrl + "?xero=connected", response.Headers.Location!.OriginalString);
        Assert.NotNull(await XeroTestHelpers.FindConnectionAsync(_factory, tenantId));
    }

    [Fact]
    public async Task Failed_callback_redirects_with_xero_failed()
    {
        await XeroTestHelpers.ResetAsync(_factory);

        var response = await NoRedirectClient()
            .GetAsync("/api/Xero/callback?code=c1&state=not-a-real-state");

        await response.AssertStatusAsync(HttpStatusCode.Redirect);
        Assert.Equal(XeroReturnUrlFactory.ReturnUrl + "?xero=failed", response.Headers.Location!.OriginalString);
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Xero_timeout_redirects_with_xero_failed()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        XeroTestHelpers.EnqueueTimeout(_factory);

        var response = await NoRedirectClient()
            .GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.Redirect);
        Assert.Equal(XeroReturnUrlFactory.ReturnUrl + "?xero=failed", response.Headers.Location!.OriginalString);
        Assert.Single(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Xero_error_redirects_with_xero_failed()
    {
        await XeroTestHelpers.ResetAsync(_factory);

        var response = await NoRedirectClient()
            .GetAsync("/api/Xero/callback?error=access_denied");

        await response.AssertStatusAsync(HttpStatusCode.Redirect);
        Assert.Equal(XeroReturnUrlFactory.ReturnUrl + "?xero=failed", response.Headers.Location!.OriginalString);
    }
}

public class XeroCallbackRedirectWithQueryTests : IClassFixture<XeroReturnUrlWithQueryFactory>
{
    private readonly XeroReturnUrlWithQueryFactory _factory;

    public XeroCallbackRedirectWithQueryTests(XeroReturnUrlWithQueryFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Redirect_appends_with_ampersand_and_keeps_fragment_last()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("q-access", "q-refresh"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.ConnectionsJson(XeroTestHelpers.NewTenantId(), "Query Co"));
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.Redirect);
        Assert.Equal(
            "http://localhost:5173/app?tab=integrations&xero=connected#xero",
            response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Failed_redirect_also_uses_ampersand()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/Xero/callback?code=c1&state=not-a-real-state");

        await response.AssertStatusAsync(HttpStatusCode.Redirect);
        Assert.Equal(
            "http://localhost:5173/app?tab=integrations&xero=failed#xero",
            response.Headers.Location!.OriginalString);
    }
}
