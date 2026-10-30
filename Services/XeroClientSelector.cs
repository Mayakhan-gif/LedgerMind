using LedgerMind.Data;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Services;

// Faisla karta hai ke Xero invoices asli API se aayein ya demo data se.
public class XeroClientSelector
{
    private readonly IConfiguration _config;
    private readonly AppDbContext _db;
    private readonly DemoXeroClient _demo;
    private readonly XeroApiClient _live;

    public XeroClientSelector(IConfiguration config, AppDbContext db, DemoXeroClient demo, XeroApiClient live)
    {
        _config = config;
        _db = db;
        _demo = demo;
        _live = live;
    }

    public async Task<IXeroClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        // Setting se zabardasti demo (presentation / bina Xero account ke)
        if (_config.GetValue("Xero:UseDemoData", false))
            return _demo;

        // Connection ho to asli Xero, warna demo
        var connected = await _db.XeroConnections.AnyAsync(cancellationToken);
        return connected ? _live : (IXeroClient)_demo;
    }
}
