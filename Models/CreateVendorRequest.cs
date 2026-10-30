using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

public class CreateVendorRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [EmailAddress]
    [MaxLength(200)]
    public string? Email { get; set; }
}