using System.Net;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Ops;

// GET /api/Audit (sirf Admin): exact-match filters, naye pehle, take 1..500
public class AuditTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Har row alag SaveChanges me, taake Id isi tarteeb me barhe
    private Task<List<AuditLog>> SeedAsync(params AuditLog[] rows) =>
        _factory.WithDbAsync(async db =>
        {
            foreach (var row in rows)
            {
                db.AuditLogs.Add(row);
                await db.SaveChangesAsync();
            }
            return rows.ToList();
        });

    private static AuditLog Row(string action, string entityType, string? entityId, string? userId = null) => new()
    {
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        UserId = userId,
        Details = "test row"
    };

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task<List<JsonElement>> GetRowsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/Audit{query}");
        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        return json.EnumerateArray().ToList();
    }

    private static List<int> Ids(IEnumerable<JsonElement> rows) =>
        rows.Select(r => r.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public async Task Filters_by_entity_type_and_id_return_only_matching_rows()
    {
        var entityType = Unique("TestEntity");
        var otherType = Unique("OtherEntity");
        var seeded = await SeedAsync(
            Row("Created", entityType, "1"),
            Row("Created", entityType, "2"),
            Row("Created", otherType, "1"),
            Row("Updated", entityType, "1"));
        var client = await TestAuth.AdminClientAsync(_factory);

        var rows = await GetRowsAsync(client, $"?entityType={entityType}&entityId=1");

        Assert.Equal(new[] { seeded[3].Id, seeded[0].Id }, Ids(rows));
        Assert.All(rows, r =>
        {
            Assert.Equal(entityType, r.GetProperty("entityType").GetString());
            Assert.Equal("1", r.GetProperty("entityId").GetString());
        });

        var byType = await GetRowsAsync(client, $"?entityType={entityType}");
        Assert.Equal(new[] { seeded[3].Id, seeded[1].Id, seeded[0].Id }, Ids(byType));
    }

    [Fact]
    public async Task Filters_by_action_and_user_id()
    {
        var action = Unique("TestAction");
        var entityType = Unique("TestEntity");
        var userA = Unique("user");
        var userB = Unique("user");
        var seeded = await SeedAsync(
            Row(action, entityType, "1", userA),
            Row(action, entityType, "2", userB),
            Row(Unique("OtherAction"), entityType, "3", userA));
        var client = await TestAuth.AdminClientAsync(_factory);

        var byActionAndUser = await GetRowsAsync(client, $"?action={action}&userId={userA}");
        var row = Assert.Single(byActionAndUser);
        Assert.Equal(seeded[0].Id, row.GetProperty("id").GetInt32());
        Assert.Equal(userA, row.GetProperty("userId").GetString());
        Assert.Equal(action, row.GetProperty("action").GetString());

        var byAction = await GetRowsAsync(client, $"?action={action}");
        Assert.Equal(new[] { seeded[1].Id, seeded[0].Id }, Ids(byAction));

        var byUser = await GetRowsAsync(client, $"?userId={userA}");
        Assert.Equal(new[] { seeded[2].Id, seeded[0].Id }, Ids(byUser));
    }

    [Fact]
    public async Task Filter_matches_exactly_not_partially()
    {
        var entityType = Unique("TestEntity");
        await SeedAsync(Row("Created", entityType, "10"), Row("Created", entityType, "100"));
        var client = await TestAuth.AdminClientAsync(_factory);

        var rows = await GetRowsAsync(client, $"?entityType={entityType}&entityId=1");
        Assert.Empty(rows);

        var partialType = await GetRowsAsync(client, $"?entityType={entityType[..10]}");
        Assert.Empty(partialType);
    }

    [Fact]
    public async Task Take_limits_rows_newest_first_and_is_clamped()
    {
        var entityType = Unique("TestEntity");
        var seeded = await SeedAsync(
            Row("A", entityType, "1"),
            Row("B", entityType, "1"),
            Row("C", entityType, "1"));
        var client = await TestAuth.AdminClientAsync(_factory);

        var two = await GetRowsAsync(client, $"?entityType={entityType}&take=2");
        Assert.Equal(new[] { seeded[2].Id, seeded[1].Id }, Ids(two));

        var zero = await GetRowsAsync(client, $"?entityType={entityType}&take=0");
        Assert.Equal(new[] { seeded[2].Id }, Ids(zero));

        var huge = await GetRowsAsync(client, $"?entityType={entityType}&take=100000");
        Assert.Equal(3, huge.Count);
    }

    [Fact]
    public async Task Without_filters_returns_newest_first_up_to_default_take()
    {
        var entityType = Unique("TestEntity");
        await SeedAsync(Row("A", entityType, "1"), Row("B", entityType, "2"));
        var client = await TestAuth.AdminClientAsync(_factory);

        var rows = await GetRowsAsync(client, "");

        var ids = Ids(rows);
        Assert.True(ids.Count >= 2);
        Assert.True(ids.Count <= 100);
        Assert.Equal(ids.OrderByDescending(i => i).ToList(), ids);
    }

    [Fact]
    public async Task FinanceManager_gets_403()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Audit");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Employee_gets_403()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Audit");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_gets_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Audit");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
