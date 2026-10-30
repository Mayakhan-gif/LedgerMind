using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Invoicing;

// Approve / reject / reopen: segregation of duties, optional body aur audit
public class InvoiceReviewTests : IClassFixture<TestWebApplicationFactory>
{
    private const string OwnWorkMessage = "Apna banaya ya submit kiya invoice khud approve/reject nahi kar sakte.";
    private const string EditedWorkMessage = "Jis invoice ko aap ne edit kiya, usay khud approve/reject nahi kar sakte.";

    private readonly TestWebApplicationFactory _factory;

    public InvoiceReviewTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Employee ka banaya aur submit kiya hua invoice (PendingApproval)
    private async Task<Invoice> PendingInvoiceAsync(string? createdBy = null, string? submittedBy = null)
    {
        var customer = await TestData.AddCustomerAsync(_factory);
        if (createdBy == null || submittedBy == null)
        {
            var employee = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
            createdBy ??= employee.Id;
            submittedBy ??= employee.Id;
        }

        return await TestData.AddInvoiceAsync(_factory, customer.Id,
            status: InvoiceStatus.PendingApproval, createdByUserId: createdBy, submittedByUserId: submittedBy);
    }

    // ---------- Approve ----------

    [Fact]
    public async Task Approve_without_body_by_another_manager_returns_200_and_sets_reviewer()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await PendingInvoiceAsync();

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/approve");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(InvoiceStatus.Approved, json.GetProperty("status").GetString());
        Assert.Equal(manager.Id, json.GetProperty("reviewedByUserId").GetString());
        Assert.False(json.IsNull("reviewedAtUtc"));
        Assert.True(json.IsNull("reviewComment"));

        var saved = await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(InvoiceStatus.Approved, saved.Status);
        Assert.Equal(manager.Id, saved.ReviewedByUserId);
        Assert.NotNull(saved.ReviewedAtUtc);
    }

    [Fact]
    public async Task Approve_writes_InvoiceApproved_audit_row_with_number_and_comment()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Admin);
        var invoice = await PendingInvoiceAsync();

        var response = await client.PostAsJsonAsync($"/api/Invoices/{invoice.Id}/approve",
            new { comment = "Rates checked" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceApproved", "Invoice", invoice.Id));
        Assert.Equal(manager.Id, audit.UserId);
        Assert.Contains(invoice.InvoiceNumber, audit.Details);
        Assert.Contains("Rates checked", audit.Details);
        Assert.Equal("Rates checked", (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).ReviewComment);
    }

    [Fact]
    public async Task Creator_cannot_approve_own_invoice()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var submitter = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var invoice = await PendingInvoiceAsync(createdBy: manager.Id, submittedBy: submitter.Id);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/approve");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(OwnWorkMessage, await response.ReadMessageAsync());
        Assert.Equal(InvoiceStatus.PendingApproval, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Submitter_cannot_approve_invoice_they_submitted()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var creator = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var invoice = await PendingInvoiceAsync(createdBy: creator.Id, submittedBy: manager.Id);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/approve");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(OwnWorkMessage, await response.ReadMessageAsync());
        Assert.Equal(InvoiceStatus.PendingApproval, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Manager_who_created_and_submitted_via_api_cannot_approve_but_colleague_can()
    {
        var (_, author) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (colleague, colleagueClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Admin);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(author, customer.Id);
        await (await InvoicingApi.PostWithoutBodyAsync(author, $"/api/Invoices/{id}/submit"))
            .AssertStatusAsync(HttpStatusCode.OK);

        var own = await InvoicingApi.PostWithoutBodyAsync(author, $"/api/Invoices/{id}/approve");
        var other = await InvoicingApi.PostWithoutBodyAsync(colleagueClient, $"/api/Invoices/{id}/approve");

        await own.AssertStatusAsync(HttpStatusCode.BadRequest);
        await other.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(colleague.Id, (await InvoicingApi.GetInvoiceAsync(_factory, id)).ReviewedByUserId);
    }

    // Manager kisi aur ka Draft badal de (e.g. raqam barha de), employee submit kare: wo manager khud approve nahi karta
    [Fact]
    public async Task Manager_who_edited_someone_elses_draft_cannot_approve_or_reject_but_colleague_can()
    {
        var (_, employeeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, editorClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (colleague, colleagueClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(employeeClient, customer.Id);
        var number = (await InvoicingApi.GetInvoiceAsync(_factory, id)).InvoiceNumber;

        var edit = await editorClient.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, number, subtotal: 9000m, taxAmount: 0m));
        await edit.AssertStatusAsync(HttpStatusCode.NoContent);
        await (await InvoicingApi.PostWithoutBodyAsync(employeeClient, $"/api/Invoices/{id}/submit"))
            .AssertStatusAsync(HttpStatusCode.OK);

        var approveByEditor = await InvoicingApi.PostWithoutBodyAsync(editorClient, $"/api/Invoices/{id}/approve");
        var rejectByEditor = await InvoicingApi.PostWithoutBodyAsync(editorClient, $"/api/Invoices/{id}/reject");

        await approveByEditor.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(EditedWorkMessage, await approveByEditor.ReadMessageAsync());
        await rejectByEditor.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(EditedWorkMessage, await rejectByEditor.ReadMessageAsync());
        Assert.Equal(InvoiceStatus.PendingApproval, (await InvoicingApi.GetInvoiceAsync(_factory, id)).Status);
        Assert.Empty(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceApproved", "Invoice", id));

        var approveByColleague = await InvoicingApi.PostWithoutBodyAsync(colleagueClient, $"/api/Invoices/{id}/approve");

        await approveByColleague.AssertStatusAsync(HttpStatusCode.OK);
        var saved = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Equal(InvoiceStatus.Approved, saved.Status);
        Assert.Equal(colleague.Id, saved.ReviewedByUserId);
    }

    [Fact]
    public async Task Employee_cannot_approve()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var invoice = await PendingInvoiceAsync();

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/approve");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal(InvoiceStatus.PendingApproval, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Approve_of_draft_invoice_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, status: InvoiceStatus.Draft);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/approve");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Sirf PendingApproval invoice approve ho sakta hai.", await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Approve_of_unknown_invoice_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{int.MaxValue}/approve");

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    // ---------- Reject ----------

    [Fact]
    public async Task Reject_with_comment_stores_review_comment_and_audit()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await PendingInvoiceAsync();

        var response = await client.PostAsJsonAsync($"/api/Invoices/{invoice.Id}/reject",
            new { comment = "  Tax amount ghalat hai  " });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(InvoiceStatus.Rejected, json.GetProperty("status").GetString());
        Assert.Equal("Tax amount ghalat hai", json.GetProperty("reviewComment").GetString());

        var saved = await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(InvoiceStatus.Rejected, saved.Status);
        Assert.Equal("Tax amount ghalat hai", saved.ReviewComment);
        Assert.Equal(manager.Id, saved.ReviewedByUserId);

        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceRejected", "Invoice", invoice.Id));
        Assert.Contains(invoice.InvoiceNumber, audit.Details);
        Assert.Contains("Tax amount ghalat hai", audit.Details);
    }

    [Fact]
    public async Task Reject_without_body_still_works()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await PendingInvoiceAsync();

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/reject");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var saved = await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(InvoiceStatus.Rejected, saved.Status);
        Assert.Null(saved.ReviewComment);
    }

    [Fact]
    public async Task Reject_with_too_long_comment_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await PendingInvoiceAsync();

        var response = await client.PostAsJsonAsync($"/api/Invoices/{invoice.Id}/reject",
            new { comment = new string('x', 501) });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(InvoiceStatus.PendingApproval, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Creator_cannot_reject_own_invoice()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var submitter = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var invoice = await PendingInvoiceAsync(createdBy: manager.Id, submittedBy: submitter.Id);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/reject");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(OwnWorkMessage, await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Employee_cannot_reject()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var invoice = await PendingInvoiceAsync();

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/reject");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    // ---------- Reopen ----------

    [Fact]
    public async Task Creator_reopens_rejected_invoice_to_draft_and_can_resubmit()
    {
        var (employee, employeeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(employeeClient, customer.Id);
        await (await InvoicingApi.PostWithoutBodyAsync(employeeClient, $"/api/Invoices/{id}/submit"))
            .AssertStatusAsync(HttpStatusCode.OK);
        await (await managerClient.PostAsJsonAsync($"/api/Invoices/{id}/reject", new { comment = "Fix tax" }))
            .AssertStatusAsync(HttpStatusCode.OK);

        var reopen = await InvoicingApi.PostWithoutBodyAsync(employeeClient, $"/api/Invoices/{id}/reopen");

        await reopen.AssertStatusAsync(HttpStatusCode.OK);
        var json = await reopen.ReadJsonAsync();
        Assert.Equal(InvoiceStatus.Draft, json.GetProperty("status").GetString());
        Assert.True(json.IsNull("submittedByUserId"));
        Assert.True(json.IsNull("reviewedByUserId"));
        Assert.True(json.IsNull("reviewedAtUtc"));
        Assert.True(json.IsNull("reviewComment"));

        var saved = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Equal(InvoiceStatus.Draft, saved.Status);
        Assert.Equal(employee.Id, saved.CreatedByUserId);
        Assert.Null(saved.SubmittedByUserId);
        Assert.Null(saved.ReviewedByUserId);
        Assert.Null(saved.ReviewedAtUtc);
        Assert.Null(saved.ReviewComment);
        Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceReopened", "Invoice", id));

        // Theek karke dobara submit
        var edit = await employeeClient.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, saved.InvoiceNumber, subtotal: 1000m, taxAmount: 170m));
        var resubmit = await InvoicingApi.PostWithoutBodyAsync(employeeClient, $"/api/Invoices/{id}/submit");

        await edit.AssertStatusAsync(HttpStatusCode.NoContent);
        await resubmit.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.PendingApproval, (await InvoicingApi.GetInvoiceAsync(_factory, id)).Status);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.PendingApproval)]
    [InlineData(InvoiceStatus.Approved)]
    [InlineData(InvoiceStatus.Cancelled)]
    public async Task Reopen_of_non_rejected_invoice_returns_400(string status)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, status: status);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/reopen");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Sirf Rejected invoice dobara khul sakta hai.", await response.ReadMessageAsync());
        Assert.Equal(status, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Employee_cannot_reopen_another_users_rejected_invoice()
    {
        var owner = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var (_, other) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id,
            status: InvoiceStatus.Rejected, createdByUserId: owner.Id, submittedByUserId: owner.Id);

        var response = await InvoicingApi.PostWithoutBodyAsync(other, $"/api/Invoices/{invoice.Id}/reopen");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal(InvoiceStatus.Rejected, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Legacy_rejected_invoice_can_be_reopened_only_by_managers()
    {
        var (_, employee) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, manager) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var legacy = await TestData.AddInvoiceAsync(_factory, customer.Id, status: InvoiceStatus.Rejected);

        var byEmployee = await InvoicingApi.PostWithoutBodyAsync(employee, $"/api/Invoices/{legacy.Id}/reopen");
        var byManager = await InvoicingApi.PostWithoutBodyAsync(manager, $"/api/Invoices/{legacy.Id}/reopen");

        await byEmployee.AssertStatusAsync(HttpStatusCode.Forbidden);
        await byManager.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Draft, (await InvoicingApi.GetInvoiceAsync(_factory, legacy.Id)).Status);
    }
}
