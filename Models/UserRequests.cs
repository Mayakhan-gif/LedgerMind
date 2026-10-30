using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

// Admin naya user banata hai (self-registration band ho tab bhi)
public class CreateUserRequest
{
    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;

    // Roles.All me se ek (controller check karta hai)
    [Required]
    public string Role { get; set; } = string.Empty;
}

public class SetRoleRequest
{
    [Required]
    public string Role { get; set; } = string.Empty;
}
