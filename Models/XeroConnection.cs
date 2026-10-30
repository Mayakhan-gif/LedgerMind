namespace LedgerMind.Models;

// Xero se jud'ne ka record. Tokens yahan ENCRYPTED rakhe jate hain.
public class XeroConnection
{
    public int Id { get; set; }

    // Xero organisation (hamare case me Demo Company) ki ID
    public string TenantId { get; set; } = string.Empty;
    public string? TenantName { get; set; }

    // Encrypted. Kabhi plain text me save/log/return nahi.
    public string AccessTokenProtected { get; set; } = string.Empty;
    public string RefreshTokenProtected { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresAtUtc { get; set; }

    public string? ConnectedByUserId { get; set; }
    public DateTime ConnectedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}