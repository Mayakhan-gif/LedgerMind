using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Invoicing;

public class CustomersTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public CustomersTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private Task<Customer?> FindCustomerAsync(int id) =>
        _factory.WithDbAsync(db => db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id));

    [Fact]
    public async Task Get_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Customers");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Employee_can_list_and_get_customers()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);

        var list = await client.GetAsync("/api/Customers");
        var single = await client.GetAsync($"/api/Customers/{customer.Id}");
        var missing = await client.GetAsync($"/api/Customers/{int.MaxValue}");

        await list.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(customer.Id, (await list.ReadJsonAsync()).EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        await single.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(customer.Name, (await single.ReadJsonAsync()).GetProperty("name").GetString());
        await missing.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Employee_can_create_and_edit_customer_and_both_are_audited()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var create = await client.PostAsJsonAsync("/api/Customers",
            new { name = "  Acme Traders  ", email = "billing@acme.test", phone = "0300-1234567" });

        await create.AssertStatusAsync(HttpStatusCode.Created);
        var json = await create.ReadJsonAsync();
        var id = json.GetProperty("id").GetInt32();
        Assert.Equal("Acme Traders", json.GetProperty("name").GetString());
        Assert.Equal("billing@acme.test", json.GetProperty("email").GetString());

        var update = await client.PutAsJsonAsync($"/api/Customers/{id}",
            new { name = "Acme Traders Ltd", email = (string?)null, phone = "   " });

        await update.AssertStatusAsync(HttpStatusCode.NoContent);
        var saved = await FindCustomerAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("Acme Traders Ltd", saved.Name);
        Assert.Null(saved.Email);
        Assert.Null(saved.Phone);

        var created = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "CustomerCreated", "Customer", id));
        Assert.Equal(employee.Id, created.UserId);
        var updated = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "CustomerUpdated", "Customer", id));
        Assert.Equal(employee.Id, updated.UserId);
    }

    [Fact]
    public async Task Create_with_invalid_values_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var noName = await client.PostAsJsonAsync("/api/Customers", new { name = "", email = "a@b.test" });
        var badEmail = await client.PostAsJsonAsync("/api/Customers", new { name = "Valid Name", email = "not-an-email" });

        await noName.AssertStatusAsync(HttpStatusCode.BadRequest);
        await badEmail.AssertStatusAsync(HttpStatusCode.BadRequest);
    }

    // Reminder emails isi address par jati hain: email badle to audit me purana aur naya dono
    [Fact]
    public async Task Email_change_is_recorded_in_audit_with_old_and_new_address()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory, name: "Mail Co", email: "old@mailco.test");

        var response = await client.PutAsJsonAsync($"/api/Customers/{customer.Id}",
            new { name = "Mail Co", email = "new@elsewhere.test", phone = "0300-1111111" });

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "CustomerUpdated", "Customer", customer.Id));
        Assert.Equal(employee.Id, audit.UserId);
        Assert.NotNull(audit.Details);
        Assert.Contains("Mail Co", audit.Details);
        Assert.Contains("old@mailco.test", audit.Details);
        Assert.Contains("new@elsewhere.test", audit.Details);
    }

    [Fact]
    public async Task Update_without_email_change_keeps_audit_details_to_the_name()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory, name: "Same Mail Co", email: "same@mailco.test");

        var response = await client.PutAsJsonAsync($"/api/Customers/{customer.Id}",
            new { name = "Same Mail Co Ltd", email = "same@mailco.test" });

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "CustomerUpdated", "Customer", customer.Id));
        Assert.Equal("Same Mail Co Ltd", audit.Details);
    }

    [Fact]
    public async Task Update_of_unknown_customer_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.PutAsJsonAsync($"/api/Customers/{int.MaxValue}", new { name = "Nobody" });

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Employee_cannot_delete_customer()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);

        var response = await client.DeleteAsync($"/api/Customers/{customer.Id}");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.NotNull(await FindCustomerAsync(customer.Id));
    }

    [Fact]
    public async Task Manager_cannot_delete_customer_with_invoices()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, status: InvoiceStatus.Cancelled);

        var response = await client.DeleteAsync($"/api/Customers/{customer.Id}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Is customer ke invoices maujood hain, delete nahi ho sakta.", await response.ReadMessageAsync());
        Assert.NotNull(await FindCustomerAsync(customer.Id));
        Assert.NotNull(await InvoicingApi.FindInvoiceAsync(_factory, invoice.Id));
        Assert.Empty(await InvoicingApi.AuditRowsAsync(_factory, "CustomerDeleted", "Customer", customer.Id));
    }

    [Fact]
    public async Task Manager_deletes_customer_without_invoices_and_it_is_audited()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);

        var response = await client.DeleteAsync($"/api/Customers/{customer.Id}");

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Null(await FindCustomerAsync(customer.Id));
        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "CustomerDeleted", "Customer", customer.Id));
        Assert.Equal(manager.Id, audit.UserId);
    }

    [Fact]
    public async Task Admin_can_delete_customer_and_unknown_id_returns_404()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var customer = await TestData.AddCustomerAsync(_factory);

        var deleted = await client.DeleteAsync($"/api/Customers/{customer.Id}");
        var missing = await client.DeleteAsync($"/api/Customers/{customer.Id}");

        await deleted.AssertStatusAsync(HttpStatusCode.NoContent);
        await missing.AssertStatusAsync(HttpStatusCode.NotFound);
    }
}
