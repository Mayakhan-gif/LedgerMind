namespace LedgerMind.Services;

// Xero se rabta nahi (connection nahi, ya token ab kaam nahi karta): Admin ko dobara connect karna hoga
public class XeroNotConnectedException : Exception
{
    public XeroNotConnectedException(string message) : base(message)
    {
    }

    public XeroNotConnectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

// Xero ki setting (ClientId/ClientSecret/RedirectUri/Scopes) mukammal nahi.
// InvalidOperationException se banti hai taake purane catch bhi chalein, lekin controller sirf isi ko
// pakarta hai: EF Core wagaira ki InvalidOperationException "setting" ka message ban kar chhup na jaye.
public class XeroConfigurationException : InvalidOperationException
{
    public XeroConfigurationException(string message) : base(message)
    {
    }
}
