namespace LedgerMind.Models;

// Raqam ke qaide jo request validation me lagte hain
public static class MoneyRules
{
    public const string DecimalsMessage = "Amount me 2 se zyada decimal nahi ho sakte.";

    public static bool HasMoreThanTwoDecimals(decimal amount) => decimal.Round(amount, 2) != amount;
}
