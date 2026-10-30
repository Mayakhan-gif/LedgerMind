using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerMind.Tests.Auth;

// Auth tests ke madadgar. Sab sirf diye gaye user ke records dekhte hain.
internal static class AuthTestHelpers
{
    public const string WrongPassword = "Wrong#Pass1";

    // Program.cs: 5 ghalat password = 15 minute ka lock
    public const int MaxFailedAttempts = 5;
    public const int LockoutSeconds = 15 * 60;

    public const string InvalidCredentialsMessage = "Email ya password galat hai.";

    public static string NewEmail(string prefix = "auth") => $"{prefix}-{Guid.NewGuid():N}@ledgermind.test";

    public static Task<HttpResponseMessage> LoginRawAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/Auth/login", new { email, password });

    public static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/Auth/refresh", new { refreshToken });

    public static Task<List<AuditLog>> AuditRowsAsync(TestWebApplicationFactory factory, string action, string entityId) =>
        factory.WithDbAsync(db => db.AuditLogs
            .AsNoTracking()
            .Where(a => a.Action == action && a.EntityId == entityId)
            .ToListAsync());

    public static Task<List<RefreshToken>> TokensOfAsync(TestWebApplicationFactory factory, string userId) =>
        factory.WithDbAsync(db => db.RefreshTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .ToListAsync());

    public static Task<ApplicationUser> GetUserAsync(TestWebApplicationFactory factory, string userId) =>
        factory.WithDbAsync(db => db.Users
            .AsNoTracking()
            .SingleAsync(u => u.Id == userId));

    public static async Task<IList<string>> RolesOfAsync(TestWebApplicationFactory factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId)
                   ?? throw new InvalidOperationException("User nahi mila.");
        return await users.GetRolesAsync(user);
    }

    // API ke zariye 5 ghalat koshishen: aakhri wali account lock karti hai (429)
    public static async Task LockOutViaLoginAsync(HttpClient client, string email)
    {
        for (var i = 1; i <= MaxFailedAttempts; i++)
        {
            var response = await LoginRawAsync(client, email, WrongPassword);
            var expected = i < MaxFailedAttempts
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.TooManyRequests;
            await response.AssertStatusAsync(expected);
        }
    }
}
