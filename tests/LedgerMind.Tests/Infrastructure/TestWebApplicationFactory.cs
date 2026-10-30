using Hangfire;
using LedgerMind.Data;
using LedgerMind.Services;
using LedgerMind.Tests.Infrastructure.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LedgerMind.Tests.Infrastructure;

// Poori API ko memory me chalata hai: EF Core InMemory database, Hangfire band,
// aur Stripe / SMTP / Groq / Xero ki jagah fakes. Har factory ka apna database hota hai.
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@ledgermind.test";
    public const string AdminPassword = "Admin#12345";
    public const string DefaultPassword = "Passw0rd!";
    public const string StripeWebhookSecret = "whsec_test_ledgermind_secret";
    public const string JwtKey = "test-signing-key-for-ledgermind-at-least-32-chars";

    private readonly string _databaseName = $"ledgermind-tests-{Guid.NewGuid():N}";

    public FakeStripeGateway FakeStripe { get; } = new();
    public FakeEmailTransport FakeEmail { get; } = new();
    public FakeBackgroundJobClient FakeJobs { get; } = new();
    public FakeHttpMessageHandler GroqHttp { get; } = new();
    public FakeHttpMessageHandler XeroHttp { get; } = new();

    // Request ke waqt parhi jane wali settings. Subclass constructor me badal sakte hain
    // (host pehli CreateClient()/Services par banta hai).
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Auth:AllowSelfRegistration"] = "true",
        ["RateLimiting:AuthPermitLimit"] = "100000",
        ["RateLimiting:RefreshPermitLimit"] = "100000",
        ["RateLimiting:ChatPermitLimit"] = "100000",
        ["Stripe:SecretKey"] = "sk_test_dummy",
        ["Stripe:WebhookSecret"] = StripeWebhookSecret,
        ["Stripe:SuccessUrl"] = "http://localhost:5173/payment/success",
        ["Stripe:CancelUrl"] = "http://localhost:5173/payment/cancel",
        ["Groq:ApiKey"] = "test-groq-key",
        ["Groq:BaseUrl"] = "https://groq.test/openai/v1/chat/completions",
        ["Xero:ClientId"] = "xero-test-client",
        ["Xero:ClientSecret"] = "xero-test-secret",
        ["Xero:RedirectUri"] = "https://localhost:7210/api/xero/callback",
        ["Xero:UseDemoData"] = "false",
        ["Smtp:Host"] = "smtp.test",
        ["Smtp:FromAddress"] = "noreply@ledgermind.test"
    };

    static TestWebApplicationFactory()
    {
        // Program.cs ye settings builder.Build() se pehle parhta hai, is liye environment variables.
        // Har factory me ek jaisi hain, is liye process-wide hona theek hai.
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "LedgerMind");
        Environment.SetEnvironmentVariable("Jwt__Audience", "LedgerMindUsers");
        Environment.SetEnvironmentVariable("Jwt__ExpiryMinutes", "15");
        Environment.SetEnvironmentVariable("Jwt__RefreshTokenDays", "7");
        Environment.SetEnvironmentVariable("Hangfire__Enabled", "false");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("DataProtection__KeysPath",
            Path.Combine(Path.GetTempPath(), "ledgermind-tests-keys"));
        Environment.SetEnvironmentVariable("SeedAdmin__Email", AdminEmail);
        Environment.SetEnvironmentVariable("SeedAdmin__Password", AdminPassword);
        Environment.SetEnvironmentVariable("SeedAdmin__FullName", "Test Admin");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            "Server=(not-used-in-tests);Database=LedgerMindTests;");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));

        builder.ConfigureTestServices(services =>
        {
            // Har test host ki apni key ring (parallel hosts ek doosre ki keys na chhedein)
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(
                    Path.Combine(Path.GetTempPath(), "ledgermind-tests-keys", _databaseName)));

            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options
                .UseInMemoryDatabase(_databaseName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            services.RemoveAll<IStripeGateway>();
            services.AddSingleton<IStripeGateway>(FakeStripe);

            services.RemoveAll<IEmailTransport>();
            services.AddSingleton<IEmailTransport>(FakeEmail);

            services.RemoveAll<IBackgroundJobClient>();
            services.AddSingleton<IBackgroundJobClient>(FakeJobs);

            services.AddHttpClient<GroqChatService>().ConfigurePrimaryHttpMessageHandler(() => GroqHttp);
            services.AddHttpClient<XeroAuthService>().ConfigurePrimaryHttpMessageHandler(() => XeroHttp);
            services.AddHttpClient<XeroApiClient>().ConfigurePrimaryHttpMessageHandler(() => XeroHttp);
        });
    }

    // Seeded data aur assertions ke liye seedha database
    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
    }
}
