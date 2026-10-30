using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LedgerMind.Data;
using LedgerMind.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Services;

public class XeroAuthService
{
    private const string AuthorizeUrl = "https://login.xero.com/identity/connect/authorize";
    private const string TokenUrl = "https://identity.xero.com/connect/token";
    private const string RevocationUrl = "https://identity.xero.com/connect/revocation";
    private const string ConnectionsUrl = "https://api.xero.com/connections";

    private const string NotConnectedMessage = "Xero connect nahi hai. Admin pehle Xero connect kare.";
    private const string ReconnectMessage = "Xero dobara connect karein";
    private const string RefreshFailedMessage = "Xero token refresh fail, dobara connect karein";

    // XeroConnections.TenantName ki lambai (AppDbContext dekhein)
    private const int TenantNameLength = 200;

    // Callback me aaya har sahih state audit me darj hota hai (EntityId = nonce), taake wahi state dobara na chale.
    // Nonce AuditLogs.EntityId (100) me poora aana chahiye.
    private const string StateUsedAction = "XeroStateUsed";
    private const string StateEntity = "XeroOAuthState";
    private const int MaxNonceLength = 100;

    // Access token expire hone se itna pehle hi refresh (raste me expire na ho)
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    // Ek waqt me ek hi refresh (ya disconnect): Xero har refresh par naya refresh token deta hai
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    // Har identity call ka apna timeout (typed client ka default 100 second bohat lamba hai).
    // Lock ka intezar is se thora zyada: lock rakhne wali call itni der me khatam ho jati hai.
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockWaitTimeout = TimeSpan.FromSeconds(45);

    private readonly IConfiguration _config;
    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly AuditService _audit;
    private readonly ILogger<XeroAuthService> _logger;
    private readonly IDataProtector _stateProtector;
    private readonly IDataProtector _tokenProtector;

    public XeroAuthService(
        IConfiguration config,
        AppDbContext db,
        HttpClient http,
        IDataProtectionProvider dataProtection,
        AuditService audit,
        ILogger<XeroAuthService> logger)
    {
        _config = config;
        _db = db;
        _http = http;
        _audit = audit;
        _logger = logger;

        // Do alag "purpose": state ka encryption aur token ka encryption ek doosre se alag
        _stateProtector = dataProtection.CreateProtector("LedgerMind.Xero.State.v1");
        _tokenProtector = dataProtection.CreateProtector("LedgerMind.Xero.Tokens.v1");
    }

    // ---------- 1. Connect link banana ----------
    public string BuildConnectUrl(string userId)
    {
        var clientId = _config["Xero:ClientId"];
        var redirectUri = _config["Xero:RedirectUri"];
        var scopes = _config["Xero:Scopes"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(redirectUri) ||
            string.IsNullOrWhiteSpace(scopes))
            throw new XeroConfigurationException("Xero ki setting (ClientId/RedirectUri/Scopes) mukammal nahi.");

        // state: userId | expiry (10 minute) | nonce, sab encrypted
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var state = _stateProtector.Protect($"{userId}|{expiry}|{nonce}");

        return $"{AuthorizeUrl}?response_type=code" +
               $"&client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&scope={Uri.EscapeDataString(scopes)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    // ---------- 2. Callback: code se token, token se tenant, phir save ----------
    public async Task<(bool Success, string Message)> HandleCallbackAsync(string? code, string? state)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return (false, "code ya state maujood nahi.");

        // state check
        string userId;
        string nonce;
        try
        {
            var parts = _stateProtector.Unprotect(state).Split('|');
            if (parts.Length != 3 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                string.IsNullOrWhiteSpace(parts[2]) ||
                parts[2].Length > MaxNonceLength ||
                !long.TryParse(parts[1], out var expiry) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
                return (false, "State expire ya ghalat hai. Connect link dobara banayein.");

            userId = parts[0];
            nonce = parts[2];
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return (false, "State ghalat hai.");
        }

        // State sirf ek dafa: leak hua link (history, proxy log, screenshot) 10 minute ke andar kisi aur
        // Xero organisation ke code ke saath dobara chala kar ghalat tenant admin ke naam par connect na ho.
        // (Bilkul ek hi waqt ki do requests dono guzar sakti hain; AuditLogs par unique index nahi.)
        var stateUsed = await _db.AuditLogs.AnyAsync(a => a.EntityType == StateEntity && a.EntityId == nonce);
        if (stateUsed)
            return (false, "Ye connect link pehle use ho chuka. Naya link banayein.");

        // Link banane ke baad Admin role hat gaya ho to connect nahi
        var isAdmin = await (
            from userRole in _db.UserRoles
            join role in _db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId && role.Name == Roles.Admin
            select userRole.UserId).AnyAsync();
        if (!isAdmin)
            return (false, "Sirf Admin Xero connect kar sakta hai.");

        // Istemal pehle hi darj: Xero call fail ho (ya setting adhoori ho) tab bhi ye state dobara nahi chalega
        await _audit.LogAsync(StateUsedAction, StateEntity, nonce, "Xero callback", userId);

        var redirectUri = _config["Xero:RedirectUri"];
        if (string.IsNullOrWhiteSpace(redirectUri))
            throw new XeroConfigurationException("Xero ki setting (RedirectUri) mukammal nahi.");

        var (clientId, clientSecret) = ClientCredentials();

        // code ko token se badalna
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        tokenReq.Headers.Authorization = BasicAuth(clientId, clientSecret);
        tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        });

        using var tokenRes = await SendWithTimeoutAsync(tokenReq);
        if (!tokenRes.IsSuccessStatusCode)
        {
            // Body log nahi karte, sirf status
            _logger.LogWarning("Xero token exchange fail hui, status {Status}", (int)tokenRes.StatusCode);
            return (false, "Xero ne token nahi diya. Redirect URI, Client ID/Secret aur scopes check karein.");
        }

        var tokens = ParseTokens(await tokenRes.Content.ReadAsStringAsync());
        var accessToken = tokens?.AccessToken;
        var refreshToken = tokens?.RefreshToken;
        var expiresIn = tokens?.ExpiresIn ?? 1800;

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            return (false, "Token mukammal nahi mila (offline_access scope check karein).");

        // Tenant (organisation) ID: Xero se poochna ke ye token kis organisation ka hai
        using var connReq = new HttpRequestMessage(HttpMethod.Get, ConnectionsUrl);
        connReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        connReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var connRes = await SendWithTimeoutAsync(connReq);
        if (!connRes.IsSuccessStatusCode)
        {
            _logger.LogWarning("Xero connections call fail hui, status {Status}", (int)connRes.StatusCode);
            return (false, "Xero se organisation ki list nahi mili.");
        }

        var (tenantId, tenantName) = ParseOrganisation(await connRes.Content.ReadAsStringAsync());
        if (string.IsNullOrWhiteSpace(tenantId))
            return (false, "Koi Xero organisation connect nahi hui.");

        if (tenantName != null && tenantName.Length > TenantNameLength)
            tenantName = tenantName[..TenantNameLength];

        // Encrypt karke save (wahi organisation dobara aaye to purani row update)
        var now = DateTime.UtcNow;
        var conn = await _db.XeroConnections.FirstOrDefaultAsync(c => c.TenantId == tenantId);
        if (conn == null)
        {
            conn = new XeroConnection { TenantId = tenantId, ConnectedAtUtc = now };
            _db.XeroConnections.Add(conn);
        }

        conn.TenantName = tenantName;
        conn.AccessTokenProtected = _tokenProtector.Protect(accessToken);
        conn.RefreshTokenProtected = _tokenProtector.Protect(refreshToken);
        conn.AccessTokenExpiresAtUtc = now.AddSeconds(expiresIn);
        conn.ConnectedByUserId = userId;
        conn.UpdatedAtUtc = now;

        // Callback me JWT nahi hota, is liye user state se
        _audit.Stage("XeroConnected", "XeroConnection", tenantId, $"Connected: {tenantName}", userId);
        await _db.SaveChangesAsync();

        return (true, $"Xero connect ho gaya: {tenantName}");
    }

    // ---------- 3. Status (tokens kabhi wapis nahi dete) ----------
    public async Task<object> GetStatusAsync()
    {
        var c = await _db.XeroConnections
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync();

        if (c == null)
            return new { connected = false, usingLiveData = false };

        return new
        {
            connected = true,
            tenantName = c.TenantName,
            accessTokenExpiresAtUtc = c.AccessTokenExpiresAtUtc,
            connectedAtUtc = c.ConnectedAtUtc,
            // Connected ho kar bhi demo setting on ho to invoices demo hi rahenge
            usingLiveData = !UseDemoData
        };
    }

    public Task<bool> IsConnectedAsync() => _db.XeroConnections.AnyAsync();

    // ---------- 4. API call ke liye taaza access token + tenant ----------
    public async Task<(string AccessToken, string TenantId)> GetAccessContextAsync(
        CancellationToken cancellationToken = default)
    {
        var conn = await _db.XeroConnections
            .OrderByDescending(c => c.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (conn == null)
            throw new XeroNotConnectedException(NotConnectedMessage);

        if (conn.AccessTokenExpiresAtUtc > DateTime.UtcNow.Add(RefreshMargin))
            return (Decrypt(conn.AccessTokenProtected), conn.TenantId);

        await AcquireLockAsync(cancellationToken);
        try
        {
            // Intezar ke dauran kisi aur request ne refresh (ya disconnect) kar diya ho
            var entry = _db.Entry(conn);
            await entry.ReloadAsync(cancellationToken);
            if (entry.State == EntityState.Detached)
                throw new XeroNotConnectedException(NotConnectedMessage);

            if (conn.AccessTokenExpiresAtUtc > DateTime.UtcNow.Add(RefreshMargin))
                return (Decrypt(conn.AccessTokenProtected), conn.TenantId);

            var accessToken = await RefreshAsync(conn, cancellationToken);
            return (accessToken, conn.TenantId);
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    // ---------- 5. Disconnect: Xero par token revoke, phir rows hatana ----------
    public async Task<bool> DisconnectAsync(string? userId)
    {
        // Refresh jari ho to uske khatam hone ka intezar: warna refresh hati hui row update karta,
        // aur rows lock ke andar parhte hain taake taaza (rotated) refresh token revoke ho
        await AcquireLockAsync(CancellationToken.None);
        try
        {
            var connections = await _db.XeroConnections.ToListAsync();
            if (connections.Count == 0)
                return false;

            var clientId = _config["Xero:ClientId"];
            var clientSecret = _config["Xero:ClientSecret"];

            foreach (var conn in connections)
            {
                // Revoke na ho sake to bhi hamare paas se connection hat jaye
                if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
                    await TryRevokeAsync(conn, clientId, clientSecret);

                _db.XeroConnections.Remove(conn);
                _audit.Stage("XeroDisconnected", "XeroConnection", conn.TenantId,
                    $"Disconnected: {conn.TenantName}", userId);
            }

            await _db.SaveChangesAsync();
            return true;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    // Lock ka intezar hamesha mehdood: atka hua Xero baqi requests ko der tak na roke
    private async Task AcquireLockAsync(CancellationToken cancellationToken)
    {
        if (!await RefreshLock.WaitAsync(LockWaitTimeout, cancellationToken))
        {
            _logger.LogWarning("Xero token lock ka intezar {Seconds} second me khatam nahi hua", LockWaitTimeout.TotalSeconds);
            throw new TimeoutException("Xero token refresh ka intezar bohat lamba ho gaya.");
        }
    }

    // Xero identity call apne timeout ke saath. Jawab poora buffer ho kar aata hai,
    // is liye timeout khatam hone ke baad bhi content parha ja sakta hai.
    private async Task<HttpResponseMessage> SendWithTimeoutAsync(
        HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(HttpTimeout);
        return await _http.SendAsync(request, cts.Token);
    }

    private async Task<string> RefreshAsync(XeroConnection conn, CancellationToken cancellationToken)
    {
        var refreshToken = Decrypt(conn.RefreshTokenProtected);
        var (clientId, clientSecret) = ClientCredentials();

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        req.Headers.Authorization = BasicAuth(clientId, clientSecret);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        // Lock ke andar hai: timeout ke saath, taake atka hua Xero sab ko na roke
        using var res = await SendWithTimeoutAsync(req, cancellationToken);
        if (!res.IsSuccessStatusCode)
        {
            // Sirf status, body/token kabhi log nahi
            _logger.LogWarning("Xero token refresh fail hui, status {Status}", (int)res.StatusCode);
            throw new XeroNotConnectedException(RefreshFailedMessage);
        }

        var tokens = ParseTokens(await res.Content.ReadAsStringAsync(cancellationToken));
        if (tokens == null)
        {
            _logger.LogWarning("Xero token refresh ka jawab mukammal nahi tha");
            throw new XeroNotConnectedException(RefreshFailedMessage);
        }

        var now = DateTime.UtcNow;
        conn.AccessTokenProtected = _tokenProtector.Protect(tokens.AccessToken);
        // Xero refresh token har dafa badalta hai; naya na aaye to purana hi rakhein
        conn.RefreshTokenProtected = _tokenProtector.Protect(tokens.RefreshToken ?? refreshToken);
        conn.AccessTokenExpiresAtUtc = now.AddSeconds(tokens.ExpiresIn);
        conn.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken);

        return tokens.AccessToken;
    }

    private async Task TryRevokeAsync(XeroConnection conn, string clientId, string clientSecret)
    {
        var refreshToken = TryDecrypt(conn.RefreshTokenProtected);
        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogWarning("Xero refresh token decrypt nahi hua, revoke ke baghair connection hata rahe hain");
            return;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, RevocationUrl);
            req.Headers.Authorization = BasicAuth(clientId, clientSecret);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = refreshToken
            });

            using var res = await SendWithTimeoutAsync(req);
            if (!res.IsSuccessStatusCode)
                _logger.LogWarning("Xero token revoke fail hui, status {Status}", (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning("Xero revoke call nahi ho saki ({Error})", ex.GetType().Name);
        }
    }

    private string Decrypt(string protectedValue)
    {
        var value = TryDecrypt(protectedValue);
        if (string.IsNullOrEmpty(value))
        {
            // Key ring badal gayi ya data kharab: token wapis nahi mil sakta
            _logger.LogWarning("Xero token decrypt nahi ho saka");
            throw new XeroNotConnectedException(ReconnectMessage);
        }

        return value;
    }

    private string? TryDecrypt(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return null;

        try
        {
            return _tokenProtector.Unprotect(protectedValue);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private bool UseDemoData => _config.GetValue("Xero:UseDemoData", false);

    private (string ClientId, string ClientSecret) ClientCredentials()
    {
        var clientId = _config["Xero:ClientId"];
        var clientSecret = _config["Xero:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new XeroConfigurationException("Xero ki setting (ClientId/ClientSecret) mukammal nahi.");

        return (clientId, clientSecret);
    }

    private static AuthenticationHeaderValue BasicAuth(string clientId, string clientSecret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

    // Token endpoint ka jawab: access_token, refresh_token, expires_in
    private static XeroTokens? ParseTokens(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var accessToken = ReadString(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            var refreshToken = ReadString(root, "refresh_token");
            var expiresIn = 1800;
            if (root.TryGetProperty("expires_in", out var ei) &&
                ei.ValueKind == JsonValueKind.Number &&
                ei.TryGetInt32(out var seconds) &&
                seconds > 0)
                expiresIn = seconds;

            return new XeroTokens(
                accessToken,
                string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken,
                expiresIn);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // /connections ka jawab: pehli ORGANISATION
    private static (string? TenantId, string? TenantName) ParseOrganisation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (null, null);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var type = ReadString(item, "tenantType");
                if (type != null && !string.Equals(type, "ORGANISATION", StringComparison.OrdinalIgnoreCase))
                    continue;

                var tenantId = ReadString(item, "tenantId");
                if (string.IsNullOrWhiteSpace(tenantId))
                    continue;

                return (tenantId, ReadString(item, "tenantName"));
            }
        }
        catch (JsonException)
        {
            // Ghalat JSON = koi organisation nahi
        }

        return (null, null);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record XeroTokens(string AccessToken, string? RefreshToken, int ExpiresIn);
}
