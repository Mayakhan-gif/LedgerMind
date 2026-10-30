using LedgerMind.Models;
using LedgerMind.Services;

namespace LedgerMind.Tests.Unit;

public class InvoiceRulesTests
{
    [Theory]
    [InlineData(1000, 0, InvoiceStatus.Approved)]
    [InlineData(1000, 0.01, InvoiceStatus.Partial)]
    [InlineData(1000, 999.99, InvoiceStatus.Partial)]
    [InlineData(1000, 1000, InvoiceStatus.Paid)]
    [InlineData(1000, 1200, InvoiceStatus.Paid)]
    [InlineData(1000, -50, InvoiceStatus.Approved)]
    public void StatusForPaid_maps_paid_amount_to_status(double total, double paid, string expected)
    {
        Assert.Equal(expected, InvoiceRules.StatusForPaid((decimal)total, (decimal)paid));
    }

    [Theory]
    [InlineData(1500.00, 150000)]
    [InlineData(0.01, 1)]
    [InlineData(10.005, 1001)]
    [InlineData(10.004, 1000)]
    [InlineData(999999999.99, 99999999999)]
    public void ToMinorUnits_rounds_half_away_from_zero(double amount, long expected)
    {
        Assert.Equal(expected, InvoiceRules.ToMinorUnits((decimal)amount));
    }

    [Fact]
    public void FromMinorUnits_is_inverse_of_ToMinorUnits()
    {
        Assert.Equal(1234.56m, InvoiceRules.FromMinorUnits(InvoiceRules.ToMinorUnits(1234.56m)));
    }

    [Fact]
    public void IsOverdue_requires_past_due_date_collectable_status_and_not_excluded()
    {
        var today = new DateTime(2026, 10, 25);
        var isOverdue = InvoiceRules.IsOverdue(today).Compile();

        Invoice Make(string status, DateTime due, bool excluded = false) =>
            new() { Status = status, DueDate = due, IsExcluded = excluded };

        Assert.True(isOverdue(Make(InvoiceStatus.Approved, today.AddDays(-1))));
        Assert.True(isOverdue(Make(InvoiceStatus.Partial, today.AddDays(-30))));

        Assert.False(isOverdue(Make(InvoiceStatus.Approved, today)));
        Assert.False(isOverdue(Make(InvoiceStatus.Approved, today.AddDays(1))));
        Assert.False(isOverdue(Make(InvoiceStatus.Approved, today.AddDays(-1), excluded: true)));
        Assert.False(isOverdue(Make(InvoiceStatus.Paid, today.AddDays(-1))));
        Assert.False(isOverdue(Make(InvoiceStatus.Draft, today.AddDays(-1))));
        Assert.False(isOverdue(Make(InvoiceStatus.PendingApproval, today.AddDays(-1))));
        Assert.False(isOverdue(Make(InvoiceStatus.Rejected, today.AddDays(-1))));
        Assert.False(isOverdue(Make(InvoiceStatus.Cancelled, today.AddDays(-1))));
    }

    [Theory]
    [InlineData(InvoiceStatus.Approved, false, true)]
    [InlineData(InvoiceStatus.Partial, false, true)]
    [InlineData(InvoiceStatus.Approved, true, false)]
    [InlineData(InvoiceStatus.Paid, false, false)]
    [InlineData(InvoiceStatus.Draft, false, false)]
    [InlineData(InvoiceStatus.Cancelled, false, false)]
    public void IsCollectable_only_for_approved_or_partial_and_not_excluded(string status, bool excluded, bool expected)
    {
        Assert.Equal(expected, InvoiceRules.IsCollectable(new Invoice { Status = status, IsExcluded = excluded }));
    }
}
