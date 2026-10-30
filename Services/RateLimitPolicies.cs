namespace LedgerMind.Services;

// [EnableRateLimiting(...)] ke naam. Limits Program.cs me config se aati hain.
public static class RateLimitPolicies
{
    // Login, register: har IP par
    public const string Auth = "auth";

    // Token refresh: har IP par, login se zyada had
    public const string Refresh = "refresh";

    // Chatbot: har user par (AI ka kharcha)
    public const string Chat = "chat";
}
