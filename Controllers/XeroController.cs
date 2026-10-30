using System.Security.Claims;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LedgerMind.Controllers;

[Authorize(Roles = Roles.Managers)]
[ApiController]
[Route("api/[controller]")]
public class XeroController : ControllerBase
{
    private const string ConfigErrorMessage = "Xero ki setting mukammal nahi.";
    private const string TimeoutMessage = "Xero ne waqt par jawab nahi diya. Thori der baad koshish karein.";

    private readonly XeroClientSelector _clients;
    private readonly XeroAuthService _auth;
    private readonly IConfiguration _config;
    private readonly ILogger<XeroController> _logger;

    public XeroController(
        XeroClientSelector clients,
        XeroAuthService auth,
        IConfiguration config,
        ILogger<XeroController> logger)
    {
        _clients = clients;
        _auth = auth;
        _config = config;
        _logger = logger;
    }

    // Sirf Admin connect shuru kar sakta hai
    [Authorize(Roles = Roles.Admin)]
    [HttpGet("connect-url")]
    public IActionResult ConnectUrl()
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            return Ok(new
            {
                url = _auth.BuildConnectUrl(userId),
                note = "Ye link browser me kholein (10 minute ke andar)."
            });
        }
        catch (XeroConfigurationException ex)
        {
            _logger.LogError(ex, "Xero configuration error");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ConfigErrorMessage });
        }
    }

    // Xero login ke baad browser yahan aata hai. Browser ke paas JWT nahi hota,
    // is liye AllowAnonymous. Hifazat 'state' se hoti hai.
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            return CallbackResult(false, $"Xero ne access nahi diya ({Shorten(error, 100)}).",
                StatusCodes.Status400BadRequest);

        try
        {
            var (success, message) = await _auth.HandleCallbackAsync(code, state);
            return CallbackResult(success, message, StatusCodes.Status400BadRequest);
        }
        catch (XeroConfigurationException ex)
        {
            _logger.LogError(ex, "Xero configuration error");
            return CallbackResult(false, ConfigErrorMessage, StatusCodes.Status500InternalServerError);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Xero callback ke dauran Xero tak pohanch nahi hui ({Error})", ex.GetType().Name);
            return CallbackResult(false, "Xero se rabta nahi ho saka. Dobara koshish karein.",
                StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException)
        {
            // Token ya /connections call ka timeout (request ka apna token aage nahi jata)
            _logger.LogWarning("Xero callback ke dauran Xero ne waqt par jawab nahi diya");
            return CallbackResult(false, TimeoutMessage, StatusCodes.Status504GatewayTimeout);
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        return Ok(await _auth.GetStatusAsync());
    }

    // Connection ho to asli Xero invoices, warna (ya UseDemoData par) demo
    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices([FromQuery] string? status, CancellationToken cancellationToken)
    {
        var client = await _clients.GetClientAsync(cancellationToken);

        List<XeroInvoiceDto> invoices;
        try
        {
            invoices = await client.GetInvoicesAsync();
        }
        catch (XeroNotConnectedException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (XeroConfigurationException ex)
        {
            // Token refresh ke liye ClientId/ClientSecret chahiye
            _logger.LogError(ex, "Xero configuration error");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ConfigErrorMessage });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Xero invoices nahi mil sake, status {Status}", (int?)ex.StatusCode);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = "Xero se invoices nahi mil sake. Thori der baad koshish karein." });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Xero ne waqt par jawab nahi diya");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = TimeoutMessage });
        }
        catch (TimeoutException)
        {
            // Doosri request ka token refresh bohat der se chal raha hai
            _logger.LogWarning("Xero token refresh ka intezar khatam");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = TimeoutMessage });
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            invoices = invoices
                .Where(i => string.Equals(i.Status, status, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return Ok(new
        {
            isDemo = client.IsDemo,
            note = client.IsDemo ? "DEMO DATA: ye asli Xero ka data nahi hai." : null,
            count = invoices.Count,
            invoices
        });
    }

    // Xero se rishta khatam: token revoke (koshish), connection rows delete, audit
    [Authorize(Roles = Roles.Admin)]
    [HttpDelete("connection")]
    public async Task<IActionResult> Disconnect()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        bool removed;
        try
        {
            removed = await _auth.DisconnectAsync(userId);
        }
        catch (TimeoutException)
        {
            // Token refresh abhi chal raha hai (lock nahi mila)
            _logger.LogWarning("Xero disconnect: token lock ka intezar khatam");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = TimeoutMessage });
        }

        if (!removed)
            return NotFound(new { message = "Xero pehle se connect nahi hai." });

        return Ok(new { message = "Xero disconnect ho gaya." });
    }

    // ReturnUrl set ho to browser wapis app par (xero=connected / xero=failed), warna JSON
    private IActionResult CallbackResult(bool success, string message, int failureStatusCode)
    {
        var returnUrl = _config["Xero:ReturnUrl"];
        if (!string.IsNullOrWhiteSpace(returnUrl))
            return Redirect(AppendQueryParameter(returnUrl.Trim(), success ? "xero=connected" : "xero=failed"));

        if (success)
            return Ok(new { message });

        return StatusCode(failureStatusCode, new { message });
    }

    private static string AppendQueryParameter(string url, string parameter)
    {
        // #fragment ho to query uske pehle aati hai
        var hashIndex = url.IndexOf('#');
        var fragment = hashIndex >= 0 ? url[hashIndex..] : string.Empty;
        var baseUrl = hashIndex >= 0 ? url[..hashIndex] : url;

        string separator;
        if (!baseUrl.Contains('?'))
            separator = "?";
        else if (baseUrl.EndsWith('?') || baseUrl.EndsWith('&'))
            separator = string.Empty;
        else
            separator = "&";

        return baseUrl + separator + parameter + fragment;
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
