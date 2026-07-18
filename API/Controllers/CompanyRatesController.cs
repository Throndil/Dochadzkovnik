using API.Data;
using API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace API.Controllers;

/// <summary>
/// "Odvody" — configurable company amounts (odvody, ubytovanie, výjazd auta…).
/// The customer edits amounts and adds his own rows; code reads well-known
/// rows by <see cref="CompanyRate.Key"/>. Manager-only.
/// </summary>
[ApiController]
[Route("api/company-rates")]
[Authorize]
public class CompanyRatesController : ControllerBase
{
    private readonly AppDbContext _db;

    public CompanyRatesController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<List<CompanyRateDto>>> List()
        => await _db.CompanyRates
            .OrderBy(r => r.Id)
            .Select(r => new CompanyRateDto { Id = r.Id, Key = r.Key, Label = r.Label, Amount = r.Amount, Unit = r.Unit, UpdatedAt = r.UpdatedAt })
            .ToListAsync();

    [HttpPost]
    public async Task<ActionResult<CompanyRateDto>> Create([FromBody] SaveCompanyRateDto dto)
    {
        var rate = new CompanyRate
        {
            Label = dto.Label.Trim(),
            Amount = dto.Amount,
            Unit = string.IsNullOrWhiteSpace(dto.Unit) ? null : dto.Unit.Trim(),
            UpdatedAt = DateTime.UtcNow
        };
        _db.CompanyRates.Add(rate);
        await _db.SaveChangesAsync();
        return new CompanyRateDto { Id = rate.Id, Key = rate.Key, Label = rate.Label, Amount = rate.Amount, Unit = rate.Unit, UpdatedAt = rate.UpdatedAt };
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<CompanyRateDto>> Update(int id, [FromBody] SaveCompanyRateDto dto)
    {
        var rate = await _db.CompanyRates.FindAsync(id);
        if (rate == null) return NotFound();

        rate.Label = dto.Label.Trim();
        rate.Amount = dto.Amount;
        rate.Unit = string.IsNullOrWhiteSpace(dto.Unit) ? null : dto.Unit.Trim();
        rate.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new CompanyRateDto { Id = rate.Id, Key = rate.Key, Label = rate.Label, Amount = rate.Amount, Unit = rate.Unit, UpdatedAt = rate.UpdatedAt };
    }

    /// <summary>Only customer-added rows (no Key) can be deleted — the seeded
    /// ones are referenced by code (výjazdy, hrubá sadzba).</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var rate = await _db.CompanyRates.FindAsync(id);
        if (rate == null) return NotFound();
        if (rate.Key != null) return BadRequest("Túto položku používa aplikácia — nedá sa zmazať, len upraviť.");
        _db.CompanyRates.Remove(rate);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    public sealed class CompanyRateDto
    {
        public int Id { get; set; }
        public string? Key { get; set; }
        public string Label { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string? Unit { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class SaveCompanyRateDto
    {
        [Required, StringLength(100, MinimumLength = 2)]
        public string Label { get; set; } = string.Empty;
        [Range(0, 1_000_000)]
        public decimal Amount { get; set; }
        [StringLength(50)]
        public string? Unit { get; set; }
    }
}
