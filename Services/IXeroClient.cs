using LedgerMind.Models;

namespace LedgerMind.Services;

public interface IXeroClient
{
    // true ho to data fake hai
    bool IsDemo { get; }

    Task<List<XeroInvoiceDto>> GetInvoicesAsync();
}