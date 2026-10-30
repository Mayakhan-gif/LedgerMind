using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LedgerMind.Models;

namespace LedgerMind.Services;

// Asli Xero Accounting API se invoices (read-only).
public class XeroApiClient : IXeroClient
{
    private const string InvoicesUrl = "https://api.xero.com/api.xro/2.0/Invoices";

    // Xero ek page par 100 invoices deta hai. Zyada se zyada 5 page (500 invoices) parhte hain.
    private const int PageSize = 100;
    private const int MaxPages = 5;

    // Purana Microsoft JSON date: /Date(1518685950940+0000)/
    private static readonly Regex MsJsonDate = new(
        @"^/Date\((-?\d+)([+-]\d{4})?\)/$",
        RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly XeroAuthService _auth;
    private readonly ILogger<XeroApiClient> _logger;

    public XeroApiClient(HttpClient http, XeroAuthService auth, ILogger<XeroApiClient> logger)
    {
        _http = http;
        _auth = auth;
        _logger = logger;
    }

    public bool IsDemo => false;

    public async Task<List<XeroInvoiceDto>> GetInvoicesAsync()
    {
        // Token expire ho raha ho to yahin refresh ho jata hai
        var (accessToken, tenantId) = await _auth.GetAccessContextAsync();

        var invoices = new List<XeroInvoiceDto>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var pageInvoices = await GetPageAsync(accessToken, tenantId, page);
            invoices.AddRange(pageInvoices);

            // Poora page nahi aaya = aakhri page
            if (pageInvoices.Count < PageSize)
                break;
        }

        return invoices;
    }

    private async Task<List<XeroInvoiceDto>> GetPageAsync(string accessToken, string tenantId, int page)
    {
        var url = $"{InvoicesUrl}?page={page.ToString(CultureInfo.InvariantCulture)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("xero-tenant-id", tenantId);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await _http.SendAsync(req);

        // Token Xero ne qubool nahi kiya (access wapis le liya gaya): dobara connect
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Xero ne access token qubool nahi kiya, status {Status}", (int)res.StatusCode);
            throw new XeroNotConnectedException("Xero ne access qubool nahi kiya, dobara connect karein");
        }

        if (!res.IsSuccessStatusCode)
        {
            // Sirf status log, body nahi
            _logger.LogWarning("Xero invoices call fail hui (page {Page}), status {Status}", page, (int)res.StatusCode);
            throw new HttpRequestException(
                $"Xero invoices call fail hui ({(int)res.StatusCode}).", null, res.StatusCode);
        }

        var json = await res.Content.ReadAsStringAsync();
        try
        {
            return ParseInvoices(json);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Xero invoices ka jawab JSON nahi tha (page {Page})", page);
            throw new HttpRequestException("Xero ka jawab samajh nahi aaya.");
        }
    }

    // Xero ka { "Invoices": [ ... ] } jawab hamare DTO me. Jo field na ho wo default.
    public static List<XeroInvoiceDto> ParseInvoices(string json)
    {
        var result = new List<XeroInvoiceDto>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Invoices", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var contactName = string.Empty;
            if (item.TryGetProperty("Contact", out var contact) && contact.ValueKind == JsonValueKind.Object)
                contactName = ReadString(contact, "Name") ?? string.Empty;

            var dto = new XeroInvoiceDto
            {
                InvoiceId = ReadString(item, "InvoiceID") ?? string.Empty,
                InvoiceNumber = ReadString(item, "InvoiceNumber") ?? string.Empty,
                ContactName = contactName,
                Status = ReadString(item, "Status") ?? string.Empty,
                DueDate = ReadDueDate(item),
                Total = ReadDecimal(item, "Total"),
                AmountPaid = ReadDecimal(item, "AmountPaid"),
                AmountDue = ReadDecimal(item, "AmountDue"),
                CurrencyCode = ReadString(item, "CurrencyCode") ?? string.Empty
            };

            var type = ReadString(item, "Type");
            if (!string.IsNullOrWhiteSpace(type))
                dto.Type = type;

            result.Add(dto);
        }

        return result;
    }

    // Pehle ISO "DueDateString" (2026-10-10T00:00:00), na mile to "DueDate" (/Date(...)/)
    private static DateTime? ReadDueDate(JsonElement item)
    {
        if (TryParseIso(ReadString(item, "DueDateString"), out var fromIso))
            return fromIso;

        var raw = ReadString(item, "DueDate");
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var match = MsJsonDate.Match(raw.Trim());
        if (match.Success)
        {
            if (!long.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var milliseconds))
                return null;

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return TryParseIso(raw, out var fallback) ? fallback : (DateTime?)null;
    }

    private static bool TryParseIso(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Xero ki tareekh me timezone nahi hota: UTC maan lete hain
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal ReadDecimal(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var number)
            ? number
            : 0m;
}
