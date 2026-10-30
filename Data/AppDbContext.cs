using LedgerMind.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    // Identity user id ki lambai (nvarchar(450)), taake user id wale columns index ho saken
    private const int UserIdLength = 450;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();
    public DbSet<XeroConnection> XeroConnections => Set<XeroConnection>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Vendor>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(200);
        });

        builder.Entity<Customer>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(30);
        });

        builder.Entity<Invoice>(e =>
        {
            e.Property(i => i.Subtotal).HasPrecision(18, 2);
            e.Property(i => i.TaxAmount).HasPrecision(18, 2);
            e.Property(i => i.Total).HasPrecision(18, 2);
            e.Property(i => i.Currency).HasMaxLength(3);
            e.Property(i => i.InvoiceNumber).HasMaxLength(50);
            e.Property(i => i.Description).HasMaxLength(500);
            e.Property(i => i.Status).HasMaxLength(20);
            e.Property(i => i.CreatedByUserId).HasMaxLength(UserIdLength);
            e.Property(i => i.SubmittedByUserId).HasMaxLength(UserIdLength);
            e.Property(i => i.ReviewedByUserId).HasMaxLength(UserIdLength);
            e.Property(i => i.ReviewComment).HasMaxLength(500);
            e.Property(i => i.RowVersion).IsRowVersion();
            e.HasIndex(i => i.InvoiceNumber).IsUnique();
            e.HasIndex(i => i.Status);

            // Customer delete karne se invoices (paise ka record) kabhi khud delete na hon
            e.HasOne(i => i.Customer)
                .WithMany()
                .HasForeignKey(i => i.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Payment>(e =>
        {
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Method).HasMaxLength(30);
            e.Property(p => p.Note).HasMaxLength(300);
            e.Property(p => p.CreatedByUserId).HasMaxLength(UserIdLength);

            e.HasOne(p => p.Invoice)
                .WithMany()
                .HasForeignKey(p => p.InvoiceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<PaymentAttempt>()
                .WithMany()
                .HasForeignKey(p => p.PaymentAttemptId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Expense>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);

            // Do managers ek hi expense ek saath approve/reject karein to doosre ko 409 mile
            e.Property(x => x.Status).HasMaxLength(20).IsConcurrencyToken();
            e.Property(x => x.ReviewedByUserId).HasMaxLength(UserIdLength);
            e.Property(x => x.ReviewComment).HasMaxLength(500);
            e.HasIndex(x => x.SubmittedByUserId);
            e.HasIndex(x => x.Status);
        });

        builder.Entity<TaskItem>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Priority).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.CreatedByUserId).HasMaxLength(UserIdLength);
            e.HasIndex(x => x.AssignedToUserId);
        });

        builder.Entity<AuditLog>(e =>
        {
            e.Property(x => x.UserId).HasMaxLength(UserIdLength);
            e.Property(x => x.UserEmail).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.Details).HasMaxLength(1000);
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        builder.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(100);
            e.Property(x => x.ReplacedByTokenHash).HasMaxLength(100);

            // Ek hi token se do refresh ek saath aayein to doosra fail ho (rotation race)
            e.Property(x => x.RevokedAtUtc).IsConcurrencyToken();

            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        builder.Entity<EmailLog>(e =>
        {
            e.Property(x => x.ToEmail).HasMaxLength(256);
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.Type).HasMaxLength(50);
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.Property(x => x.EntityId).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(500);
            e.HasIndex(x => new { x.Type, x.EntityId });
        });

        builder.Entity<PaymentAttempt>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.RefundedAmount).HasPrecision(18, 2);
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.ProviderSessionId).HasMaxLength(200);
            e.Property(x => x.ProviderPaymentIntentId).HasMaxLength(200);
            e.Property(x => x.ProviderRefundId).HasMaxLength(200);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.Property(x => x.CreatedByUserId).HasMaxLength(UserIdLength);
            e.Property(x => x.ResolvedByUserId).HasMaxLength(UserIdLength);
            e.Property(x => x.ResolutionNote).HasMaxLength(500);
            e.HasIndex(x => x.ProviderSessionId).IsUnique();
            e.HasIndex(x => x.ProviderPaymentIntentId);
            e.HasIndex(x => x.InvoiceId);
            e.HasOne<Invoice>()
                .WithMany()
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PaymentWebhookEvent>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.EventId).HasMaxLength(100);
            e.Property(x => x.EventType).HasMaxLength(100);
            e.Property(x => x.Outcome).HasMaxLength(300);
            e.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
        });

        builder.Entity<XeroConnection>(e =>
        {
            e.Property(x => x.TenantId).HasMaxLength(50);
            e.Property(x => x.TenantName).HasMaxLength(200);
            e.Property(x => x.ConnectedByUserId).HasMaxLength(UserIdLength);
            e.HasIndex(x => x.TenantId).IsUnique();
        });
    }
}
