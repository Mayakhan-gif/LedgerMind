using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

public class RefreshRequest
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}