using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LedgerMind.Data;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace LedgerMind.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    // Anjaan email aur ghalat password ka ek hi jawab, taake email ka pata na chale
    private const string InvalidCredentialsMessage = "Email ya password galat hai.";
    private const string UserEntity = "User";

    // Takrao (concurrency) ho to itni baar dobara koshish: reuse par saare tokens
    // band karna, aur login par ghalat koshishon ki ginti badalna
    private const int ConflictAttempts = 3;

    // Anjaan email ki ghalat koshishon ki ginti (memory me). Asal account ki ginti kabhi
    // khud kam nahi hoti, is liye ye bhi lambi rakhi hai (har nayi koshish par 24 ghante aur).
    private const string UnknownEmailKeyPrefix = "login-unknown:";
    private static readonly TimeSpan UnknownEmailCounterLifetime = TimeSpan.FromHours(24);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _config;
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly IMemoryCache _cache;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        IConfiguration config,
        AppDbContext db,
        AuditService audit,
        IMemoryCache cache)
    {
        _userManager = userManager;
        _config = config;
        _db = db;
        _audit = audit;
        _cache = cache;
    }

    // Anjaan email ka "lock" haal: asal account jaisa (5 ghalat = 15 minute)
    private sealed class UnknownEmailLock
    {
        public int Failures;
        public DateTimeOffset? LockoutEnd;
    }

    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        // Production me band (appsettings.json); accounts admin banata hai
        if (!_config.GetValue("Auth:AllowSelfRegistration", false))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Khud register karna band hai. Admin se account banwayein." });

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName
        };

        // User, role aur audit ek saath: role na lage to user bhi na bane
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));

        var roleResult = await _userManager.AddToRoleAsync(user, Roles.Employee);
        if (!roleResult.Succeeded)
            return BadRequest(roleResult.Errors.Select(e => e.Description));

        // Request anonymous hai, is liye user id/email khud dete hain
        await _audit.LogAsync("UserRegistered", UserEntity, user.Id,
            $"Role: {Roles.Employee}", userId: user.Id, userEmail: user.Email);

        await transaction.CommitAsync();

        return Ok(new { message = "User register ho gaya." });
    }

    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        if (user == null)
            return await UnknownEmailLoginAsync(request);

        // Lock ke dauran password check hi nahi (sahi ho ya ghalat, jawab ek)
        if (await _userManager.IsLockedOutAsync(user))
        {
            await _audit.LogAsync("LoginWhileLockedOut", UserEntity, user.Id,
                "Lock ke dauran login ki koshish.", userId: user.Id, userEmail: user.Email);
            return await LockedOutAsync(user);
        }

        if (!await _userManager.CheckPasswordAsync(user, request.Password))
        {
            // Program.cs: 5 ghalat koshishen = 15 minute ka lock
            user = await UpdateUserWithRetryAsync(user, u => _userManager.AccessFailedAsync(u));

            if (await _userManager.IsLockedOutAsync(user))
            {
                await _audit.LogAsync("AccountLockedOut", UserEntity, user.Id,
                    "Ghalat password ki had poori ho gayi.", userId: user.Id, userEmail: user.Email);
                return await LockedOutAsync(user);
            }

            var failedCount = await _userManager.GetAccessFailedCountAsync(user);
            await _audit.LogAsync("LoginFailed", UserEntity, user.Id,
                $"Ghalat password ({failedCount} baar).", userId: user.Id, userEmail: user.Email);

            return Unauthorized(new { message = InvalidCredentialsMessage });
        }

        user = await UpdateUserWithRetryAsync(user, u => _userManager.ResetAccessFailedCountAsync(u));

        // Isi dauran parallel ghalat koshishon ne lock laga diya ho to login nahi
        if (await _userManager.IsLockedOutAsync(user))
        {
            await _audit.LogAsync("LoginWhileLockedOut", UserEntity, user.Id,
                "Sahi password, lekin isi dauran account lock ho gaya.", userId: user.Id, userEmail: user.Email);
            return await LockedOutAsync(user);
        }

        // Audit naye refresh token ke saath hi save hota hai (IssueTokensAsync)
        _audit.Stage("LoginSucceeded", UserEntity, user.Id, userId: user.Id, userEmail: user.Email);

        return Ok(await IssueTokensAsync(user));
    }

    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        var hash = HashToken(request.RefreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (stored == null)
            return Unauthorized(new { message = "Refresh token ghalat hai." });

        if (stored.RevokedAtUtc != null)
        {
            // Rotate ho chuka token dobara aaya: shak hai ke chori hua,
            // is liye us user ke saare active tokens band kar dete hain.
            if (stored.ReplacedByTokenHash != null)
            {
                await RevokeAllOnReuseAsync(stored.UserId);
                return Unauthorized(new { message = "Token dobara use hua. Dobara login karein." });
            }

            // Logout, role badalne ya pichhle reuse se band hua token
            // (Request anonymous hai, is liye user id khud dete hain)
            await _audit.LogAsync("RefreshFailed", UserEntity, stored.UserId,
                "Token pehle se band (logout/role change/reuse).", userId: stored.UserId);
            return Unauthorized(new { message = "Session khatam ho chuka. Dobara login karein." });
        }

        if (!stored.IsActive)
        {
            await _audit.LogAsync("RefreshFailed", UserEntity, stored.UserId,
                "Token expire ho gaya.", userId: stored.UserId);
            return Unauthorized(new { message = "Refresh token expire ho gaya." });
        }

        var user = await _userManager.FindByIdAsync(stored.UserId);
        if (user == null)
        {
            await _audit.LogAsync("RefreshFailed", UserEntity, stored.UserId,
                "User maujood nahi.", userId: stored.UserId);
            return Unauthorized(new { message = "User maujood nahi." });
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            await _audit.LogAsync("RefreshFailed", UserEntity, user.Id,
                "Account lock hai.", userId: user.Id, userEmail: user.Email);
            return Unauthorized(new { message = "Account abhi lock hai. Baad me login karein." });
        }

        // Audit naye token aur purane ke revoke ke saath ek hi SaveChanges me (IssueTokensAsync)
        _audit.Stage("TokenRefreshed", UserEntity, user.Id, userId: user.Id, userEmail: user.Email);

        // Rotation: purana token band, naya jari. RevokedAtUtc concurrency token hai,
        // is liye ek hi token se do refresh ek saath aayein to doosra yahan fail hota hai.
        stored.RevokedAtUtc = DateTime.UtcNow;
        try
        {
            return Ok(await IssueTokensAsync(user, stored));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nakam revoke, naya token aur staged audit context se hata kar sirf nakami ka audit
            _db.ChangeTracker.Clear();
            await _audit.LogAsync("RefreshFailed", UserEntity, user.Id,
                "Token ek saath do baar use hua (takrao).", userId: user.Id, userEmail: user.Email);
            return Unauthorized(new { message = "Token pehle hi use ho chuka. Dobara login karein." });
        }
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var hash = HashToken(request.RefreshToken);

        // Sirf apna token band ho sakta hai
        var stored = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId);

        // Ye token kisi aur ne pehle hi rotate kar liya: naya token shayad chor ke paas hai,
        // is liye saare tokens band (logout audit bhi usi SaveChanges me)
        if (stored != null && stored.RevokedAtUtc != null && stored.ReplacedByTokenHash != null)
        {
            await RevokeAllOnReuseAsync(stored.UserId, () =>
                _audit.Stage("Logout", UserEntity, userId, "Rotate ho chuka token ke saath logout."));
            return Ok(new { message = "Logout ho gaya." });
        }

        var revoked = false;
        if (stored != null && stored.RevokedAtUtc == null)
        {
            stored.RevokedAtUtc = DateTime.UtcNow;
            revoked = true;
        }

        // Token band karna aur audit ek hi SaveChanges me
        _audit.Stage("Logout", UserEntity, userId,
            revoked ? "Refresh token band kiya." : "Token pehle se band ya na-maloom.");
        await _db.SaveChangesAsync();

        return Ok(new { message = "Logout ho gaya." });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(new
        {
            id = User.FindFirstValue(ClaimTypes.NameIdentifier),
            email = User.FindFirstValue(ClaimTypes.Email),
            fullName = User.FindFirstValue(ClaimTypes.Name),
            roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value)
        });
    }

    // Anjaan email bhi asal account ki tarah "lock" hota hai: 1-4 ghalat = 401, 5vin par aur
    // 15 minute tak 429 + Retry-After. Warna 5 koshishon me 429 bata deta ke account maujood hai.
    // Koi user nahi banta. Audit row bina user/entity id ke, normalized email ke saath (S6: har
    // nakam login ka record, taake anjaan emails par password spraying bhi nazar aaye).
    private async Task<IActionResult> UnknownEmailLoginAsync(LoginRequest request)
    {
        var lockout = _userManager.Options.Lockout;
        var normalizedEmail = _userManager.NormalizeEmail(request.Email);
        var key = UnknownEmailKeyPrefix + normalizedEmail;
        var state = _cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = UnknownEmailCounterLifetime;
            // Cache ki had (Program.cs SizeLimit): har entry 1 ginti
            entry.Size = 1;
            return new UnknownEmailLock();
        })!;

        // Asal account jaisa: lock ke dauran password ka kaam hi nahi
        DateTimeOffset? lockedUntil;
        lock (state)
            lockedUntil = state.LockoutEnd > DateTimeOffset.UtcNow ? state.LockoutEnd : null;
        if (lockedUntil.HasValue)
        {
            await _audit.LogAsync("UnknownEmailLoginWhileLockedOut", UserEntity, null,
                "Anjaan email: lock ke dauran login ki koshish.", userEmail: normalizedEmail);
            return LockedOutResult(lockedUntil);
        }

        // Anjaan email par bhi utna hi hashing ka kaam, taake jawab ki raftaar
        // (timing) se bhi pata na chale ke email maujood hai ya nahi
        _ = _userManager.PasswordHasher.HashPassword(new ApplicationUser(), request.Password);

        // Identity ki AccessFailedAsync jaisa: had poori = lock, aur ginti dobara 0
        lock (state)
        {
            state.Failures++;
            if (state.Failures >= lockout.MaxFailedAccessAttempts)
            {
                state.Failures = 0;
                state.LockoutEnd = DateTimeOffset.UtcNow.Add(lockout.DefaultLockoutTimeSpan);
                lockedUntil = state.LockoutEnd;
            }
        }
        if (lockedUntil.HasValue)
        {
            await _audit.LogAsync("UnknownEmailLockedOut", UserEntity, null,
                "Anjaan email par ghalat koshishon ki had poori.", userEmail: normalizedEmail);
            return LockedOutResult(lockedUntil);
        }

        await _audit.LogAsync("LoginFailedUnknownEmail", UserEntity, null,
            "Anjaan email par login ki koshish.", userEmail: normalizedEmail);
        return Unauthorized(new { message = InvalidCredentialsMessage });
    }

    private async Task<IActionResult> LockedOutAsync(ApplicationUser user) =>
        LockedOutResult(await _userManager.GetLockoutEndDateAsync(user));

    // 429 + Retry-After (lock khatam hone tak ke seconds, kam az kam 1)
    private IActionResult LockedOutResult(DateTimeOffset? lockoutEnd)
    {
        var remaining = lockoutEnd.HasValue
            ? (lockoutEnd.Value - DateTimeOffset.UtcNow).TotalSeconds
            : 1d;
        var seconds = (int)Math.Clamp(Math.Ceiling(remaining), 1d, int.MaxValue);

        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            message = "Bohot zyada ghalat koshishen. Account kuch der ke liye lock hai.",
            retryAfterSeconds = seconds
        });
    }

    // Saare active tokens band + audit ek hi SaveChanges me. Isi waqt koi aur
    // request token badal de (concurrency) to dobara parh kar koshish.
    // stageAlso: saath save hone wala koi aur audit (har koshish par dobara stage hota hai).
    private async Task RevokeAllOnReuseAsync(string userId, Action? stageAlso = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var now = DateTime.UtcNow;
            var active = await _db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
                .ToListAsync();

            foreach (var token in active)
                token.RevokedAtUtc = now;

            _audit.Stage("RefreshTokenReuseDetected", UserEntity, userId,
                $"Rotate ho chuka token dobara aaya. {active.Count} active token band kiye.",
                userId: userId);
            stageAlso?.Invoke();

            try
            {
                await _db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < ConflictAttempts)
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    // Identity ka update (AccessFailed/ResetAccessFailedCount) ConcurrencyStamp ke takrao par
    // Failed deta hai aur user context me Modified reh jata hai; agla SaveChanges (audit ya
    // refresh token) wahi purana update dobara bhej kar 409 deta. Is liye nakami par user
    // context se hata kar DB se taaza parhte hain aur dobara koshish karte hain, taake
    // parallel ghalat koshishon ki ginti bhi gum na ho.
    private async Task<ApplicationUser> UpdateUserWithRetryAsync(
        ApplicationUser user, Func<ApplicationUser, Task<IdentityResult>> update)
    {
        for (var attempt = 1; attempt <= ConflictAttempts; attempt++)
        {
            var result = await update(user);
            if (result.Succeeded)
                return user;

            _db.Entry(user).State = EntityState.Detached;
            user = await _userManager.FindByIdAsync(user.Id) ?? user;
        }

        // Har koshish fail: taaza (be-badla) user wapas, taake agla SaveChanges saaf rahe
        return user;
    }

    private async Task<object> IssueTokensAsync(ApplicationUser user, RefreshToken? old = null)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var accessToken = CreateAccessToken(user, roles);

        var rawRefresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var newHash = HashToken(rawRefresh);

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = newHash,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(_config.GetValue("Jwt:RefreshTokenDays", 7d))
        });

        if (old != null)
            old.ReplacedByTokenHash = newHash;

        // Naya token, purane ka revoke aur staged audit ek hi SaveChanges me
        await _db.SaveChangesAsync();

        return new { token = accessToken, refreshToken = rawRefresh, roles };
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private string CreateAccessToken(ApplicationUser user, IList<string> roles)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Email, user.Email!),
            new Claim(ClaimTypes.Name, user.FullName)
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        // Role badalne par stamp badalta hai: Program.cs isay DB se mila kar purana token rad kar sakta hai
        if (!string.IsNullOrEmpty(user.SecurityStamp))
            claims.Add(new Claim(SecurityStampClaim.Type, SecurityStampClaim.Hash(user.SecurityStamp)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_config.GetValue("Jwt:ExpiryMinutes", 15d)),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
