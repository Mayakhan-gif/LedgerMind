using System.Text;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerMind.Tests.Xero;

// Xero tests ke liye mushtarka madadgar: connection seed karna, state banana, fake Xero jawab
internal static class XeroTestHelpers
{
    public const string TokenPurpose = "LedgerMind.Xero.Tokens.v1";
    public const string StatePurpose = "LedgerMind.Xero.State.v1";

    public const string TokenUrl = "https://identity.xero.com/connect/token";
    public const string RevocationUrl = "https://identity.xero.com/connect/revocation";
    public const string ConnectionsUrl = "https://api.xero.com/connections";
    public const string InvoicesUrl = "https://api.xero.com/api.xro/2.0/Invoices";

    public static readonly string[] DemoInvoiceNumbers =
    {
        "DEMO-INV-001", "DEMO-INV-002", "DEMO-BILL-001", "DEMO-INV-003"
    };

    // Har test se pehle: fake HTTP khali aur is class ke database me koi Xero connection nahi.
    // (Har test class ka apna factory aur apna database hota hai, aur class ke tests ek ek kar ke chalte hain.)
    public static async Task ResetAsync(TestWebApplicationFactory factory)
    {
        factory.XeroHttp.Reset();
        await factory.WithDbAsync(async db =>
        {
            db.XeroConnections.RemoveRange(await db.XeroConnections.ToListAsync());
            await db.SaveChangesAsync();
        });
    }

    public static IDataProtector TokenProtector(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(TokenPurpose);

    public static IDataProtector Protector(TestWebApplicationFactory factory, string purpose) =>
        factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose);

    // Wahi format jo XeroAuthService.BuildConnectUrl banata hai: userId|expiry|nonce
    public static string BuildState(TestWebApplicationFactory factory, string userId, DateTimeOffset expiresAt) =>
        Protector(factory, StatePurpose).Protect($"{userId}|{expiresAt.ToUnixTimeSeconds()}|{Guid.NewGuid():N}");

    public static string NewTenantId() => $"t-{Guid.NewGuid():N}";

    // Xero ka timeout: HttpClient timeout par TaskCanceledException deta hai (yahan foran, bina intezar)
    public static void EnqueueTimeout(TestWebApplicationFactory factory) =>
        factory.XeroHttp.Enqueue(_ => throw new TaskCanceledException("Xero test timeout"));

    public static string ExpectedBasicAuth(TestWebApplicationFactory factory)
    {
        var raw = $"{factory.Settings["Xero:ClientId"]}:{factory.Settings["Xero:ClientSecret"]}";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    // Tokens asli service ki tarah encrypt karke connection row
    public static async Task<XeroConnection> SeedConnectionAsync(
        TestWebApplicationFactory factory,
        string accessToken,
        string refreshToken,
        DateTime accessTokenExpiresAtUtc,
        string? tenantId = null,
        string tenantName = "Seeded Co")
    {
        var protector = TokenProtector(factory);
        var now = DateTime.UtcNow;

        var connection = new XeroConnection
        {
            TenantId = tenantId ?? NewTenantId(),
            TenantName = tenantName,
            AccessTokenProtected = protector.Protect(accessToken),
            RefreshTokenProtected = protector.Protect(refreshToken),
            AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
            ConnectedAtUtc = now,
            UpdatedAtUtc = now
        };

        await factory.WithDbAsync(async db =>
        {
            db.XeroConnections.Add(connection);
            await db.SaveChangesAsync();
        });

        return connection;
    }

    public static Task<XeroConnection?> FindConnectionAsync(TestWebApplicationFactory factory, string tenantId) =>
        factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId);
        });

    public static Task<List<AuditLog>> AuditRowsAsync(TestWebApplicationFactory factory, string action, string entityId) =>
        factory.WithDbAsync(async db =>
        {
            return await db.AuditLogs
                .AsNoTracking()
                .Where(a => a.Action == action && a.EntityId == entityId)
                .ToListAsync();
        });

    // Xero token endpoint ka jawab
    public static string TokenJson(string accessToken, string refreshToken, int expiresIn = 1800) =>
        JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            refresh_token = refreshToken,
            expires_in = expiresIn,
            token_type = "Bearer"
        });

    // Xero /connections ka jawab
    public static string ConnectionsJson(string tenantId, string tenantName) =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                id = Guid.NewGuid().ToString(),
                tenantId,
                tenantName,
                tenantType = "ORGANISATION"
            }
        });

    // Xero Invoices endpoint ka ek page (PascalCase, jaisa Xero bhejta hai)
    public static string InvoicesPageJson(int count, string prefix, string status = "AUTHORISED") =>
        JsonSerializer.Serialize(new
        {
            Status = "OK",
            Invoices = Enumerable.Range(1, count)
                .Select(i => new
                {
                    Type = "ACCREC",
                    InvoiceID = $"{prefix}-id-{i}",
                    InvoiceNumber = $"{prefix}-{i}",
                    Contact = new { ContactID = $"{prefix}-c-{i}", Name = $"Contact {i}" },
                    Status = status,
                    DueDateString = "2026-10-10T00:00:00",
                    Total = 100m,
                    AmountPaid = 0m,
                    AmountDue = 100m,
                    CurrencyCode = "PKR"
                })
                .ToArray()
        });

    // Connect URL (ya kisi bhi URL) ki query se ek value
    public static string? QueryValue(string url, string name)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
            return null;

        foreach (var pair in url[(queryStart + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;

            if (pair[..eq] == name)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return null;
    }
}
