namespace LedgerMind.Services;

// Stripe ki setting (SecretKey/WebhookSecret) mukammal nahi.
// InvalidOperationException se banti hai taake purane catch bhi chalein, lekin controller sirf isi ko
// pakarta hai: EF Core wagaira ki InvalidOperationException "setting" ka message ban kar chhup na jaye.
public sealed class StripeConfigurationException : InvalidOperationException
{
    public StripeConfigurationException(string message) : base(message)
    {
    }
}
