using System.Security.Cryptography;
using System.Text;

namespace LedgerMind.Services;

// Access token me user ke security stamp ka hash. Role badalne (UsersController.SetRole) par stamp badalta hai,
// is liye Program.cs ka JWT check (OnTokenValidated) purana token foran rad kar sakta hai.
// Asal stamp token me nahi jata: Identity ke email/2FA codes isi stamp se bante hain.
public static class SecurityStampClaim
{
    public const string Type = "sstamp";

    public static string Hash(string securityStamp) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp)));
}
