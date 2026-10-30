using LedgerMind.Data;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Controllers;

[Authorize(Roles = Roles.Admin)]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private const string UserEntity = "User";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public UsersController(UserManager<ApplicationUser> userManager, AppDbContext db, AuditService audit)
    {
        _userManager = userManager;
        _db = db;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await _userManager.Users.OrderBy(u => u.Email).ToListAsync();

        // Har user ke roles ek hi query me (har user par alag query nahi)
        var rolePairs = await (
            from userRole in _db.UserRoles
            join role in _db.Roles on userRole.RoleId equals role.Id
            select new { userRole.UserId, role.Name }).ToListAsync();

        var rolesByUser = rolePairs
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Name ?? string.Empty).OrderBy(n => n).ToList());

        var now = DateTimeOffset.UtcNow;
        var result = users
            .Select(u => ToResponse(
                u,
                rolesByUser.TryGetValue(u.Id, out var roles) ? roles : new List<string>(),
                now))
            .ToList();

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null)
            return NotFound(new { message = "User nahi mila." });

        var roles = await _userManager.GetRolesAsync(user);
        return Ok(ToResponse(user, roles, DateTimeOffset.UtcNow));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request)
    {
        if (!Roles.All.Contains(request.Role))
            return BadRequest(new { message = "Role ghalat hai." });

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName
        };

        // User, role aur audit ek saath: role na lage to user bhi na bane
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var created = await _userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
            return BadRequest(created.Errors.Select(e => e.Description));

        var added = await _userManager.AddToRoleAsync(user, request.Role);
        if (!added.Succeeded)
            return BadRequest(added.Errors.Select(e => e.Description));

        _audit.Stage("UserCreated", UserEntity, user.Id, $"Email: {user.Email}, role: {request.Role}");
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Created($"/api/Users/{user.Id}",
            ToResponse(user, new[] { request.Role }, DateTimeOffset.UtcNow));
    }

    [HttpPost("{id}/role")]
    public async Task<IActionResult> SetRole(string id, SetRoleRequest request)
    {
        if (!Roles.All.Contains(request.Role))
            return BadRequest(new { message = "Role ghalat hai." });

        // Admin ki ginti se commit tak sab ek Serializable transaction me. Warna do admin ek
        // saath ek doosre (ya khud) ko hatayein to dono ko 2 Admin dikhte aur koi na bachta.
        // Ab ginti wali rows lock rehti hain; aisi takkar me ek request ruk kar 409 paati hai.
        // InMemory (tests) me isolation level nahi hota, wahan saada transaction.
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable)
            : await _db.Database.BeginTransactionAsync();

        var user = await _userManager.FindByIdAsync(id);
        if (user == null)
            return NotFound(new { message = "User nahi mila." });

        var currentRoles = await _userManager.GetRolesAsync(user);

        // Pehle se yahi akela role hai: kuch badalna nahi (tokens bhi nahi chhedte)
        if (currentRoles.Count == 1 && currentRoles[0] == request.Role)
            return Ok(new { message = $"Role {request.Role} set ho gaya." });

        // Aakhri Admin hata diya to koi admin nahi bachega
        if (currentRoles.Contains(Roles.Admin) && request.Role != Roles.Admin)
        {
            var admins = await _userManager.GetUsersInRoleAsync(Roles.Admin);
            if (admins.Count <= 1)
                return BadRequest(new { message = "Kam az kam ek Admin hona zaroori hai." });
        }

        var removed = await _userManager.RemoveFromRolesAsync(user, currentRoles);
        if (!removed.Succeeded)
            return BadRequest(removed.Errors.Select(e => e.Description));

        var added = await _userManager.AddToRoleAsync(user, request.Role);
        if (!added.Succeeded)
            return BadRequest(added.Errors.Select(e => e.Description));

        // Naya security stamp: access token me purana stamp hai, is liye (Program.cs ke stamp check ke saath)
        // purana token purane role ke saath foran band ho jata hai, 15 minute expiry ka intezar nahi
        var stamped = await _userManager.UpdateSecurityStampAsync(user);
        if (!stamped.Succeeded)
            return BadRequest(stamped.Errors.Select(e => e.Description));

        // Purane refresh tokens band: naya role naye login se hi milega
        var now = DateTime.UtcNow;
        var activeTokens = await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAtUtc == null)
            .ToListAsync();
        foreach (var token in activeTokens)
            token.RevokedAtUtc = now;

        var previous = currentRoles.Count == 0 ? "(koi nahi)" : string.Join(", ", currentRoles);
        _audit.Stage("RoleChanged", UserEntity, user.Id,
            $"{previous} -> {request.Role}. {activeTokens.Count} refresh token band kiye.");

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new { message = $"Role {request.Role} set ho gaya." });
    }

    [HttpPost("{id}/unlock")]
    public async Task<IActionResult> Unlock(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null)
            return NotFound(new { message = "User nahi mila." });

        var previousEnd = user.LockoutEnd;

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Jis user par lockout hi enabled nahi, woh lock ho hi nahi sakta
        if (await _userManager.GetLockoutEnabledAsync(user))
        {
            var cleared = await _userManager.SetLockoutEndDateAsync(user, null);
            if (!cleared.Succeeded)
                return BadRequest(cleared.Errors.Select(e => e.Description));
        }

        var reset = await _userManager.ResetAccessFailedCountAsync(user);
        if (!reset.Succeeded)
            return BadRequest(reset.Errors.Select(e => e.Description));

        _audit.Stage("UserUnlocked", UserEntity, user.Id,
            previousEnd.HasValue
                ? $"Lock tha: {previousEnd.Value.ToString("O")}"
                : "Lock nahi tha.");

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new { message = "User unlock ho gaya." });
    }

    private static object ToResponse(ApplicationUser user, IEnumerable<string> roles, DateTimeOffset now) => new
    {
        id = user.Id,
        email = user.Email,
        fullName = user.FullName,
        roles,
        // Sirf tab jab lock abhi chal raha ho
        lockedOutUntil = user.LockoutEnabled && user.LockoutEnd > now ? user.LockoutEnd : null
    };
}
