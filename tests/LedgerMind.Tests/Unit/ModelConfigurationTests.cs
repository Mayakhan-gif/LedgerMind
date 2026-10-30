using LedgerMind.Data;
using LedgerMind.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LedgerMind.Tests.Unit;

// InMemory provider foreign keys, rowversion aur lengths enforce nahi karta,
// is liye asli SQL Server wali configuration ko model metadata se check karte hain.
public class ModelConfigurationTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(not-used);Database=ModelOnly;")
            .Options;

        using var db = new AppDbContext(options);
        return db.Model;
    }

    private static IEntityType Entity<T>(IModel model) =>
        model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} model me nahi");

    [Fact]
    public void Deleting_a_customer_never_cascades_to_invoices()
    {
        var fk = Entity<Invoice>(BuildModel()).GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(Customer));

        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void Deleting_an_invoice_never_cascades_to_payments_or_attempts()
    {
        var model = BuildModel();

        var paymentFk = Entity<Payment>(model).GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(Invoice));
        var attemptFk = Entity<PaymentAttempt>(model).GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(Invoice));

        Assert.Equal(DeleteBehavior.Restrict, paymentFk.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, attemptFk.DeleteBehavior);
    }

    [Fact]
    public void Payment_links_to_its_stripe_attempt_with_restrict()
    {
        var fk = Entity<Payment>(BuildModel()).GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(PaymentAttempt));

        Assert.Equal(nameof(Payment.PaymentAttemptId), fk.Properties.Single().Name);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.False(fk.IsRequired);
    }

    [Fact]
    public void Invoice_has_rowversion_concurrency_token()
    {
        var rowVersion = Entity<Invoice>(BuildModel()).FindProperty(nameof(Invoice.RowVersion))!;

        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
    }

    [Fact]
    public void Expense_status_is_a_concurrency_token()
    {
        var status = Entity<Expense>(BuildModel()).FindProperty(nameof(Expense.Status))!;

        Assert.True(status.IsConcurrencyToken);
    }

    [Fact]
    public void Refresh_token_revocation_is_a_concurrency_token()
    {
        var revoked = Entity<RefreshToken>(BuildModel()).FindProperty(nameof(RefreshToken.RevokedAtUtc))!;

        Assert.True(revoked.IsConcurrencyToken);
    }

    [Fact]
    public void Unique_indexes_exist_for_business_keys()
    {
        var model = BuildModel();

        bool HasUniqueIndex<T>(params string[] properties) =>
            Entity<T>(model).GetIndexes().Any(i =>
                i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(properties));

        Assert.True(HasUniqueIndex<Invoice>(nameof(Invoice.InvoiceNumber)));
        Assert.True(HasUniqueIndex<PaymentAttempt>(nameof(PaymentAttempt.ProviderSessionId)));
        Assert.True(HasUniqueIndex<PaymentWebhookEvent>(nameof(PaymentWebhookEvent.Provider), nameof(PaymentWebhookEvent.EventId)));
        Assert.True(HasUniqueIndex<RefreshToken>(nameof(RefreshToken.TokenHash)));
        Assert.True(HasUniqueIndex<XeroConnection>(nameof(XeroConnection.TenantId)));
    }

    [Theory]
    [InlineData(typeof(Customer), nameof(Customer.Name), 200)]
    [InlineData(typeof(Customer), nameof(Customer.Phone), 30)]
    [InlineData(typeof(Vendor), nameof(Vendor.Name), 200)]
    [InlineData(typeof(Invoice), nameof(Invoice.Status), 20)]
    [InlineData(typeof(Invoice), nameof(Invoice.Description), 500)]
    [InlineData(typeof(Payment), nameof(Payment.Method), 30)]
    [InlineData(typeof(Payment), nameof(Payment.Note), 300)]
    [InlineData(typeof(Expense), nameof(Expense.Status), 20)]
    [InlineData(typeof(AuditLog), nameof(AuditLog.Details), 1000)]
    [InlineData(typeof(PaymentAttempt), nameof(PaymentAttempt.ResolutionNote), 500)]
    public void String_columns_have_length_limits(Type entity, string property, int maxLength)
    {
        var prop = BuildModel().FindEntityType(entity)!.FindProperty(property)!;

        Assert.Equal(maxLength, prop.GetMaxLength());
    }

    [Theory]
    [InlineData(typeof(Invoice), nameof(Invoice.Total))]
    [InlineData(typeof(Payment), nameof(Payment.Amount))]
    [InlineData(typeof(PaymentAttempt), nameof(PaymentAttempt.Amount))]
    [InlineData(typeof(PaymentAttempt), nameof(PaymentAttempt.RefundedAmount))]
    [InlineData(typeof(Expense), nameof(Expense.Amount))]
    public void Money_columns_are_decimal_18_2(Type entity, string property)
    {
        var prop = BuildModel().FindEntityType(entity)!.FindProperty(property)!;

        Assert.Equal(18, prop.GetPrecision());
        Assert.Equal(2, prop.GetScale());
    }
}
