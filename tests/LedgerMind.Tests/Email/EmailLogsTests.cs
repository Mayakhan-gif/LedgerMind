using System.Net;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Email;

// GET /api/Email/logs (sirf Admin), filters type aur entityId
public class EmailLogsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EmailLogsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Har row alag SaveChanges me, taake Id isi tarteeb me barhe
    private Task<List<EmailLog>> SeedAsync(params EmailLog[] logs) =>
        _factory.WithDbAsync(async db =>
        {
            foreach (var log in logs)
            {
                db.EmailLogs.Add(log);
                await db.SaveChangesAsync();
            }
            return logs.ToList();
        });

    private static EmailLog Log(string type, string? entityId, string status = "Sent") => new()
    {
        ToEmail = "someone@ledgermind.test",
        Subject = "subject",
        Type = type,
        EntityType = "Invoice",
        EntityId = entityId,
        Status = status
    };

    private static List<JsonElement> Rows(JsonElement json) => json.EnumerateArray().ToList();

    [Fact]
    public async Task Admin_can_filter_logs_by_type_and_entity_id()
    {
        var entityId = $"test-{Guid.NewGuid():N}";
        var otherEntity = $"test-{Guid.NewGuid():N}";
        var seeded = await SeedAsync(
            Log("InvoiceReminder", entityId),
            Log("Welcome", entityId),
            Log("InvoiceReminder", entityId, "Failed"),
            Log("InvoiceReminder", otherEntity));
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await client.GetAsync($"/api/Email/logs?type=InvoiceReminder&entityId={entityId}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var rows = Rows(await response.ReadJsonAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("InvoiceReminder", r.GetProperty("type").GetString());
            Assert.Equal(entityId, r.GetProperty("entityId").GetString());
        });

        // Naye pehle
        Assert.Equal(seeded[2].Id, rows[0].GetProperty("id").GetInt32());
        Assert.Equal(seeded[0].Id, rows[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Entity_filter_alone_returns_every_type_for_that_entity()
    {
        var entityId = $"test-{Guid.NewGuid():N}";
        await SeedAsync(Log("InvoiceReminder", entityId), Log("Welcome", entityId), Log("Welcome", "unrelated"));
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await client.GetAsync($"/api/Email/logs?entityId={entityId}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var rows = Rows(await response.ReadJsonAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(entityId, r.GetProperty("entityId").GetString()));
    }

    [Fact]
    public async Task Take_is_clamped_to_at_least_one()
    {
        var entityId = $"test-{Guid.NewGuid():N}";
        var seeded = await SeedAsync(Log("InvoiceReminder", entityId), Log("InvoiceReminder", entityId));
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await client.GetAsync($"/api/Email/logs?entityId={entityId}&take=0");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var row = Assert.Single(Rows(await response.ReadJsonAsync()));
        Assert.Equal(seeded[1].Id, row.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Admin_without_filters_gets_newest_first()
    {
        await SeedAsync(Log("InvoiceReminder", "a"), Log("InvoiceReminder", "b"));
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await client.GetAsync("/api/Email/logs?take=200");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var ids = Rows(await response.ReadJsonAsync()).Select(r => r.GetProperty("id").GetInt32()).ToList();
        Assert.True(ids.Count >= 2);
        Assert.True(ids.Count <= 200);
        Assert.Equal(ids.OrderByDescending(i => i).ToList(), ids);
    }

    [Fact]
    public async Task FinanceManager_gets_403()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Email/logs");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Employee_gets_403()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Email/logs");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_gets_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Email/logs");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
