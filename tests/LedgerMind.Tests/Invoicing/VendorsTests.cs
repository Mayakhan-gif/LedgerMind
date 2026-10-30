using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Invoicing;

public class VendorsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public VendorsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private Task<int> AddVendorAsync() =>
        _factory.WithDbAsync(async db =>
        {
            var vendor = new Vendor { Name = $"Vendor {Guid.NewGuid():N}"[..18], Email = "vendor@ledgermind.test" };
            db.Vendors.Add(vendor);
            await db.SaveChangesAsync();
            return vendor.Id;
        });

    private Task<Vendor?> FindVendorAsync(int id) =>
        _factory.WithDbAsync(db => db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id));

    [Fact]
    public async Task Without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Vendors");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Employee_gets_403_on_every_vendor_endpoint_including_get()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var vendorId = await AddVendorAsync();

        var list = await client.GetAsync("/api/Vendors");
        var single = await client.GetAsync($"/api/Vendors/{vendorId}");
        var create = await client.PostAsJsonAsync("/api/Vendors", new { name = "Should not exist" });
        var update = await client.PutAsJsonAsync($"/api/Vendors/{vendorId}", new { name = "Changed" });
        var delete = await client.DeleteAsync($"/api/Vendors/{vendorId}");

        await list.AssertStatusAsync(HttpStatusCode.Forbidden);
        await single.AssertStatusAsync(HttpStatusCode.Forbidden);
        await create.AssertStatusAsync(HttpStatusCode.Forbidden);
        await update.AssertStatusAsync(HttpStatusCode.Forbidden);
        await delete.AssertStatusAsync(HttpStatusCode.Forbidden);

        var vendor = await FindVendorAsync(vendorId);
        Assert.NotNull(vendor);
        Assert.NotEqual("Changed", vendor.Name);
    }

    [Fact]
    public async Task Manager_can_create_read_update_and_delete_vendor_with_audit()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var create = await client.PostAsJsonAsync("/api/Vendors",
            new { name = "  Paper Supplies Co  ", email = "sales@paper.test" });
        await create.AssertStatusAsync(HttpStatusCode.Created);
        var id = (await create.ReadJsonAsync()).GetProperty("id").GetInt32();

        var list = await client.GetAsync("/api/Vendors");
        await list.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(id, (await list.ReadJsonAsync()).EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));

        var single = await client.GetAsync($"/api/Vendors/{id}");
        await single.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("Paper Supplies Co", (await single.ReadJsonAsync()).GetProperty("name").GetString());

        var update = await client.PutAsJsonAsync($"/api/Vendors/{id}",
            new { name = "Paper Supplies Ltd", email = "accounts@paper.test" });
        await update.AssertStatusAsync(HttpStatusCode.NoContent);
        var saved = await FindVendorAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("Paper Supplies Ltd", saved.Name);
        Assert.Equal("accounts@paper.test", saved.Email);

        var delete = await client.DeleteAsync($"/api/Vendors/{id}");
        await delete.AssertStatusAsync(HttpStatusCode.NoContent);
        var gone = await client.GetAsync($"/api/Vendors/{id}");
        await gone.AssertStatusAsync(HttpStatusCode.NotFound);
        Assert.Null(await FindVendorAsync(id));

        foreach (var action in new[] { "VendorCreated", "VendorUpdated", "VendorDeleted" })
        {
            var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, action, "Vendor", id));
            Assert.Equal(manager.Id, audit.UserId);
        }
    }

    [Fact]
    public async Task Admin_can_list_vendors()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var vendorId = await AddVendorAsync();

        var response = await client.GetAsync("/api/Vendors");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(vendorId, (await response.ReadJsonAsync()).EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task Create_with_invalid_email_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.PostAsJsonAsync("/api/Vendors", new { name = "Bad Email Vendor", email = "nope" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_and_delete_of_unknown_vendor_return_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var update = await client.PutAsJsonAsync($"/api/Vendors/{int.MaxValue}", new { name = "Ghost" });
        var delete = await client.DeleteAsync($"/api/Vendors/{int.MaxValue}");

        await update.AssertStatusAsync(HttpStatusCode.NotFound);
        await delete.AssertStatusAsync(HttpStatusCode.NotFound);
    }
}
