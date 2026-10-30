using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.Dashboard;
using LedgerMind.Data;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Zaroori secret maujood hai? Warna saaf message ke saath start hi na ho
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
    throw new InvalidOperationException(
        "Jwt:Key set nahi hai ya 32 characters se chhoti hai. Manage User Secrets me daalein.");

// Sirf tests ke liye band kiya jata hai (SQL Server ke baghair chal saken). Band ho to
// JobsController ko IBackgroundJobClient khud register karna hoga (tests fake lagate hain).
var hangfireEnabled = builder.Configuration.GetValue("Hangfire:Enabled", true);

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// AuthController: anjaan email ki ghalat koshishon ki ginti (asal account jaisa lock).
// Har nayi email ek entry banati hai (attacker ki marzi ki), is liye had: SizeLimit ke saath
// har entry ko Size dena zaroori hai (AuthController Size = 1 deta hai).
builder.Services.AddMemoryCache(options => options.SizeLimit = 100_000);

builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<PaymentLedger>();
builder.Services.AddScoped<IEmailTransport, SmtpEmailTransport>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<ChatToolService>();
builder.Services.AddScoped<BackgroundJobs>();
builder.Services.AddScoped<IStripeGateway, StripeGateway>();
builder.Services.AddScoped<StripePaymentService>();
builder.Services.AddScoped<DemoXeroClient>();
builder.Services.AddScoped<XeroClientSelector>();
builder.Services.AddHttpClient<XeroAuthService>();
builder.Services.AddHttpClient<XeroApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<GroqChatService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddEndpointsApiExplorer();

// Frontend ke address config se (production me apna domain daalein)
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (allowedOrigins == null || allowedOrigins.Length == 0)
    allowedOrigins = new[] { "http://localhost:5173" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("Retry-After"));
});

// Reverse proxy (nginx, IIS ARR, load balancer) ke peeche asal client IP (rate limit isi par).
// Sirf config me likhe bharosemand proxies ka X-Forwarded-For maana jata hai, warna koi bhi IP jhoota bata de.
var knownProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>()
                   ?? Array.Empty<string>();
var useForwardedHeaders = knownProxies.Length > 0;
if (useForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        foreach (var proxy in knownProxies)
        {
            if (IPAddress.TryParse(proxy, out var address))
                options.KnownProxies.Add(address);
        }
    });
}

// Login aur chatbot par had (brute force aur AI kharche se bachao).
// Limits config se tab parhi jati hain jab kisi IP/user ka limiter pehli dafa banta hai.
// Reverse proxy ke peeche IP proxy ka hota hai: wahan forwarded headers sahi set karein.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Bohot zyada requests. Thori der baad koshish karein." },
            cancellationToken);
    };

    options.AddPolicy(RateLimitPolicies.Auth, httpContext =>
    {
        var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimiting:AuthPermitLimit", 10),
            Window = TimeSpan.FromSeconds(config.GetValue("RateLimiting:AuthWindowSeconds", 60)),
            QueueLimit = 0
        });
    });

    // Refresh har tab se har ~15 minute aata hai: login se zyada had, warna ek office (ek IP) ke log phans jayein
    options.AddPolicy(RateLimitPolicies.Refresh, httpContext =>
    {
        var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimiting:RefreshPermitLimit", 60),
            Window = TimeSpan.FromSeconds(config.GetValue("RateLimiting:RefreshWindowSeconds", 60)),
            QueueLimit = 0
        });
    });

    options.AddPolicy(RateLimitPolicies.Chat, httpContext =>
    {
        var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var key = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                  ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimiting:ChatPermitLimit", 20),
            Window = TimeSpan.FromSeconds(config.GetValue("RateLimiting:ChatWindowSeconds", 60)),
            QueueLimit = 0
        });
    });
});

builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Login se mila token yahan paste karein"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

if (hangfireEnabled)
{
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connectionString));
    builder.Services.AddHangfireServer();
}

// Xero tokens isi key ring se encrypt hote hain. Keys ek pakki jagah rakhi jati hain
// (restart/deploy ke baad bhi wahi), aur khud bhi encrypted: certificate (har machine/instance
// par chalta hai) ho to us se, warna Windows par DPAPI se. Linux/container par certificate
// na ho to keys bina encryption ke likhi jati hain (production me neeche warning).
var dpKeysPathDefaulted = string.IsNullOrWhiteSpace(builder.Configuration["DataProtection:KeysPath"]);
var keysPath = dpKeysPathDefaulted
    ? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")
    : builder.Configuration["DataProtection:KeysPath"]!;

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("LedgerMind")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Password sirf user-secrets / environment variable me, appsettings me nahi
var dpCertPath = builder.Configuration["DataProtection:CertificatePath"];
var dpKeysUnencrypted = false;
if (!string.IsNullOrWhiteSpace(dpCertPath))
{
    var dpCert = new X509Certificate2(dpCertPath, builder.Configuration["DataProtection:CertificatePassword"]);
    dataProtection.ProtectKeysWithCertificate(dpCert);
}
else if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi();
else
    dpKeysUnencrypted = true;

builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;

    // 5 ghalat password = 15 minute ka lock
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            // Default 5 minute ki chhoot bohot zyada thi
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // Role badalne par user ka security stamp badalta hai (UsersController). Token me us stamp ka hash
        // hota hai: match na kare (ya user delete ho chuka) to purana token foran rad, 15 minute ka intezar nahi.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var stampHash = context.Principal?.FindFirstValue(SecurityStampClaim.Type);

                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(stampHash))
                {
                    context.Fail("Token purana hai. Dobara login karein.");
                    return;
                }

                var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(userId);

                if (user?.SecurityStamp == null ||
                    !string.Equals(SecurityStampClaim.Hash(user.SecurityStamp), stampHash, StringComparison.Ordinal))
                {
                    context.Fail("Token purana hai. Dobara login karein.");
                }
            }
        };
    });

var app = builder.Build();

// Production me localhost wali settings ke saath chupchap chalna (customer payment ke baad
// localhost par, CORS localhost ke liye, host filtering band) ghalti hai: saaf message ke saath start hi na ho.
// Testing aur Development par asar nahi.
if (app.Environment.IsProduction())
{
    if (dpKeysUnencrypted)
        app.Logger.LogWarning(
            "Data Protection keys bina encryption ke {Path} me hain. DataProtection:CertificatePath set karein.",
            keysPath);
    if (dpKeysPathDefaulted)
        app.Logger.LogWarning(
            "DataProtection:KeysPath set nahi: keys content root ({Path}) me hain aur saaf redeploy ya container " +
            "dobara banne par mit jayengi (phir Xero tokens parhe nahi jayenge).",
            keysPath);

    static bool IsLocalOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("localhost", StringComparison.OrdinalIgnoreCase);

    var cfg = app.Configuration;
    var problems = new List<string>();

    // Khali ya list me kahin bhi "*" ho to host filtering band hai
    var hosts = cfg["AllowedHosts"];
    if (string.IsNullOrWhiteSpace(hosts) ||
        hosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("*"))
        problems.Add("AllowedHosts");

    if (allowedOrigins.Any(IsLocalOrEmpty))
        problems.Add("Cors:AllowedOrigins");

    // Stripe/Xero sirf tab jab un ki keys set hon (bina integration ke deployment bhi chal sake)
    if (!string.IsNullOrWhiteSpace(cfg["Stripe:SecretKey"]) &&
        (IsLocalOrEmpty(cfg["Stripe:SuccessUrl"]) || IsLocalOrEmpty(cfg["Stripe:CancelUrl"])))
        problems.Add("Stripe:SuccessUrl/CancelUrl");

    if (!string.IsNullOrWhiteSpace(cfg["Xero:ClientId"]) && IsLocalOrEmpty(cfg["Xero:RedirectUri"]))
        problems.Add("Xero:RedirectUri");

    // ReturnUrl khali ho to Xero connect ke baad Admin ke browser me kacha JSON aata hai, SPA par wapsi nahi
    if (!string.IsNullOrWhiteSpace(cfg["Xero:ClientId"]) && IsLocalOrEmpty(cfg["Xero:ReturnUrl"]))
        problems.Add("Xero:ReturnUrl");

    // Same-host reverse proxy ke peeche har request "local" lagti hai, is liye LocalRequestsOnlyAuthorizationFilter
    // sab ko andar aane deta hai. Dashboard on karna hai to asal client IP ke liye ReverseProxy:KnownProxies zaroori
    // (aur proxy X-Forwarded-For bheje).
    if (hangfireEnabled && cfg.GetValue("Hangfire:DashboardEnabled", false) && !useForwardedHeaders)
        problems.Add("Hangfire:DashboardEnabled (ReverseProxy:KnownProxies zaroori)");

    if (problems.Count > 0)
        throw new InvalidOperationException(
            "Production setting adhoori ya localhost par: " + string.Join(", ", problems));
}

// Database migrate, roles aur pehla admin (sirf tab jab pehle se na hon)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational())
            await db.Database.MigrateAsync();
    }

    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    foreach (var role in Roles.All)
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    // Email aur password dono user-secrets me (SeedAdmin:Email, SeedAdmin:Password)
    var adminEmail = app.Configuration["SeedAdmin:Email"];
    var adminPassword = app.Configuration["SeedAdmin:Password"];

    if (!string.IsNullOrEmpty(adminEmail) && !string.IsNullOrEmpty(adminPassword))
    {
        var admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin == null)
        {
            admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                FullName = app.Configuration["SeedAdmin:FullName"] ?? "Admin"
            };
            var created = await userManager.CreateAsync(admin, adminPassword);
            if (created.Succeeded)
                await userManager.AddToRoleAsync(admin, Roles.Admin);
            else
                logger.LogError("Seed admin nahi ban saka: {Errors}",
                    string.Join("; ", created.Errors.Select(e => e.Description)));
        }
    }
    else
    {
        logger.LogWarning("SeedAdmin:Email ya SeedAdmin:Password set nahi. Pehla admin nahi banaya gaya.");
    }
}

if (useForwardedHeaders)
    app.UseForwardedHeaders();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

// Bunyadi security headers har jawab par. OnStarting is liye ke exception handler
// error jawab banate waqt pehle se lage headers saaf kar deta hai.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";

        // API sirf JSON deti hai: koi script/style/frame nahi. Swagger aur Hangfire ke apne HTML pages hain.
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/swagger") && !path.StartsWithSegments("/hangfire"))
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

        // Maali (financial) JSON kabhi browser ya proxy cache me na rahe
        if (path.StartsWithSegments("/api") && !headers.ContainsKey("Cache-Control"))
            headers.CacheControl = "no-store";

        return Task.CompletedTask;
    });
    await next();
});

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (hangfireEnabled)
{
    // Dashboard sirf Development me (ya config se on karne par), aur sirf local computer se.
    // Proxy ke peeche har request "local" lagti hai, is liye production me band rakhein
    // (on ho aur ReverseProxy:KnownProxies na ho to upar wala Production check start hi nahi hone deta).
    if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Hangfire:DashboardEnabled", false))
    {
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new IDashboardAuthorizationFilter[] { new LocalRequestsOnlyAuthorizationFilter() },
            IsReadOnlyFunc = _ => !app.Environment.IsDevelopment()
        });
    }

    // Recurring jobs (dobara chalane par update ho jati hain, duplicate nahi banti)
    var recurring = app.Services.GetRequiredService<IRecurringJobManager>();
    recurring.AddOrUpdate<BackgroundJobs>(
        "overdue-invoice-check",
        j => j.CheckOverdueInvoicesAsync(),
        Cron.Daily(3));
    recurring.AddOrUpdate<BackgroundJobs>(
        "cleanup-old-refresh-tokens",
        j => j.CleanupOldRefreshTokensAsync(),
        Cron.Weekly(DayOfWeek.Sunday, 4));
}

app.MapControllers();

app.Run();

// Integration tests (WebApplicationFactory<Program>) ke liye
public partial class Program { }
