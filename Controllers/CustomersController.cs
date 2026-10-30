using LedgerMind.Data;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class CustomersController : ControllerBase
{
    private const string EntityType = "Customer";

    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public CustomersController(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<List<Customer>>> GetAll()
    {
        return await _db.Customers.OrderBy(c => c.Id).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Customer>> GetById(int id)
    {
        var customer = await _db.Customers.FindAsync(id);
        if (customer == null) return NotFound();
        return customer;
    }

    [HttpPost]
    public async Task<ActionResult<Customer>> Create(CustomerRequest request)
    {
        // Id save ke baad milti hai: customer aur uska audit ek hi transaction me.
        // Execution strategy ke andar, taake SQL Server retry on hone par bhi user transaction chale.
        var strategy = _db.Database.CreateExecutionStrategy();
        var customer = await strategy.ExecuteAsync(async () =>
        {
            // Retry par pichli adhoori koshish ka kuch bhi context me na rahe
            _db.ChangeTracker.Clear();

            var created = new Customer
            {
                Name = request.Name.Trim(),
                Email = Clean(request.Email),
                Phone = Clean(request.Phone),
                CreatedAtUtc = DateTime.UtcNow
            };

            await using var transaction = await _db.Database.BeginTransactionAsync();

            _db.Customers.Add(created);
            await _db.SaveChangesAsync();

            _audit.Stage("CustomerCreated", EntityType, created.Id.ToString(), created.Name);
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();
            return created;
        });

        return CreatedAtAction(nameof(GetById), new { id = customer.Id }, customer);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, CustomerRequest request)
    {
        var customer = await _db.Customers.FindAsync(id);
        if (customer == null) return NotFound();

        var oldEmail = customer.Email;

        customer.Name = request.Name.Trim();
        customer.Email = Clean(request.Email);
        customer.Phone = Clean(request.Phone);

        // Reminder emails (invoice number aur baqaya) isi email par jati hain: badli ho to audit me purani aur nayi dono
        var details = string.Equals(oldEmail, customer.Email, StringComparison.Ordinal)
            ? customer.Name
            : $"{customer.Name}; email: {oldEmail ?? "-"} -> {customer.Email ?? "-"}";

        _audit.Stage("CustomerUpdated", EntityType, customer.Id.ToString(), details);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Sirf manager. Invoices wala customer delete nahi hota (paise ka record mehfooz rahe).
    [Authorize(Roles = Roles.Managers)]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var customer = await _db.Customers.FindAsync(id);
        if (customer == null) return NotFound();

        var hasInvoices = await _db.Invoices.AnyAsync(i => i.CustomerId == id);
        if (hasInvoices)
            return BadRequest(new { message = "Is customer ke invoices maujood hain, delete nahi ho sakta." });

        _db.Customers.Remove(customer);
        _audit.Stage("CustomerDeleted", EntityType, customer.Id.ToString(), customer.Name);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Khali ya sirf spaces wali value null
    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
