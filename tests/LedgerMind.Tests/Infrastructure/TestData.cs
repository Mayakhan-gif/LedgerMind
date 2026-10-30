using LedgerMind.Models;

namespace LedgerMind.Tests.Infrastructure;

// Database me seedha test data (API ke qaidon se guzre baghair, setup ke liye)
public static class TestData
{
    public static string UniqueInvoiceNumber(string prefix = "INV") => $"{prefix}-{Guid.NewGuid():N}";

    public static Task<Customer> AddCustomerAsync(
        TestWebApplicationFactory factory, string? name = null, string? email = "customer@ledgermind.test") =>
        factory.WithDbAsync(async db =>
        {
            var customer = new Customer
            {
                Name = name ?? $"Customer {Guid.NewGuid():N}"[..20],
                Email = email,
                Phone = "0300-0000000"
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            return customer;
        });

    public static Task<Invoice> AddInvoiceAsync(
        TestWebApplicationFactory factory,
        int customerId,
        string status = InvoiceStatus.Draft,
        decimal subtotal = 1000m,
        decimal taxAmount = 0m,
        string currency = "PKR",
        DateTime? invoiceDate = null,
        DateTime? dueDate = null,
        bool isExcluded = false,
        string? createdByUserId = null,
        string? submittedByUserId = null) =>
        factory.WithDbAsync(async db =>
        {
            var invoice = new Invoice
            {
                InvoiceNumber = UniqueInvoiceNumber(),
                CustomerId = customerId,
                InvoiceDate = invoiceDate ?? DateTime.UtcNow.Date,
                DueDate = dueDate ?? DateTime.UtcNow.Date.AddDays(30),
                Currency = currency,
                Subtotal = subtotal,
                TaxAmount = taxAmount,
                Total = subtotal + taxAmount,
                Status = status,
                IsExcluded = isExcluded,
                CreatedByUserId = createdByUserId,
                SubmittedByUserId = submittedByUserId
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            return invoice;
        });

    public static Task<Payment> AddPaymentAsync(
        TestWebApplicationFactory factory, int invoiceId, decimal amount, string method = "Cash", int? paymentAttemptId = null) =>
        factory.WithDbAsync(async db =>
        {
            var payment = new Payment
            {
                InvoiceId = invoiceId,
                Amount = amount,
                PaymentDate = DateTime.UtcNow,
                Method = method,
                PaymentAttemptId = paymentAttemptId
            };
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
            return payment;
        });

    public static Task<Expense> AddExpenseAsync(
        TestWebApplicationFactory factory,
        string submittedByUserId,
        string status = ExpenseStatus.Draft,
        decimal amount = 500m,
        string currency = "PKR",
        string category = "Travel",
        DateTime? expenseDate = null) =>
        factory.WithDbAsync(async db =>
        {
            var expense = new Expense
            {
                Title = $"Expense {Guid.NewGuid():N}"[..20],
                Category = category,
                Amount = amount,
                Currency = currency,
                ExpenseDate = expenseDate ?? DateTime.UtcNow.Date,
                Status = status,
                SubmittedByUserId = submittedByUserId
            };
            db.Expenses.Add(expense);
            await db.SaveChangesAsync();
            return expense;
        });

    public static Task<TaskItem> AddTaskAsync(
        TestWebApplicationFactory factory,
        string assignedToUserId,
        string createdByUserId,
        string status = Models.TaskStatus.Todo,
        DateTime? dueDate = null) =>
        factory.WithDbAsync(async db =>
        {
            var task = new TaskItem
            {
                Title = $"Task {Guid.NewGuid():N}"[..17],
                Priority = TaskPriority.Medium,
                Status = status,
                DueDate = dueDate,
                AssignedToUserId = assignedToUserId,
                CreatedByUserId = createdByUserId
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            return task;
        });

    public static Task<PaymentAttempt> AddPaymentAttemptAsync(
        TestWebApplicationFactory factory,
        int invoiceId,
        decimal amount,
        string currency = "PKR",
        string status = PaymentAttemptStatus.Pending,
        string? sessionId = null,
        string? paymentIntentId = null) =>
        factory.WithDbAsync(async db =>
        {
            var attempt = new PaymentAttempt
            {
                InvoiceId = invoiceId,
                Provider = "Stripe",
                ProviderSessionId = sessionId ?? $"cs_test_{Guid.NewGuid():N}",
                ProviderPaymentIntentId = paymentIntentId,
                Amount = amount,
                Currency = currency,
                Status = status
            };
            db.PaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            return attempt;
        });
}
