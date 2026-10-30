using LedgerMind.Data;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Controllers;

// Vendors sirf managers ke liye (dekhna bhi)
[Authorize(Roles = Roles.Managers)]
[ApiController]
[Route("api/[controller]")]
public class VendorsController : ControllerBase
{
    private const string EntityType = "Vendor";

    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public VendorsController(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<List<Vendor>>> GetAll()
    {
        return await _db.Vendors.OrderBy(v => v.Id).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Vendor>> GetById(int id)
    {
        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null) return NotFound();
        return vendor;
    }

    [HttpPost]
    public async Task<ActionResult<Vendor>> Create(CreateVendorRequest request)
    {
        // Id save ke baad milti hai: vendor aur uska audit ek hi transaction me.
        // Execution strategy ke andar, taake SQL Server retry on hone par bhi user transaction chale.
        var strategy = _db.Database.CreateExecutionStrategy();
        var vendor = await strategy.ExecuteAsync(async () =>
        {
            // Retry par pichli adhoori koshish ka kuch bhi context me na rahe
            _db.ChangeTracker.Clear();

            var created = new Vendor
            {
                Name = request.Name.Trim(),
                Email = Clean(request.Email),
                CreatedAtUtc = DateTime.UtcNow
            };

            await using var transaction = await _db.Database.BeginTransactionAsync();

            _db.Vendors.Add(created);
            await _db.SaveChangesAsync();

            _audit.Stage("VendorCreated", EntityType, created.Id.ToString(), created.Name);
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();
            return created;
        });

        return CreatedAtAction(nameof(GetById), new { id = vendor.Id }, vendor);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateVendorRequest request)
    {
        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null) return NotFound();

        vendor.Name = request.Name.Trim();
        vendor.Email = Clean(request.Email);

        _audit.Stage("VendorUpdated", EntityType, vendor.Id.ToString(), vendor.Name);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var vendor = await _db.Vendors.FindAsync(id);
        if (vendor == null) return NotFound();

        _db.Vendors.Remove(vendor);
        _audit.Stage("VendorDeleted", EntityType, vendor.Id.ToString(), vendor.Name);
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
