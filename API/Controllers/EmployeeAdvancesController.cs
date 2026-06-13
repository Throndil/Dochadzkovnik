using API.Data;
using API.DTOs;
using API.Filters;
using API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// CRUD for employee cash advances (Zálohy) — see PAYROLL_AND_PNL_PLAN.md.
/// Admin-only and behind the PayrollAndPnL feature flag. The kiosk surface
/// MUST NOT consume or expose this data.
/// </summary>
[ApiController]
[Route("api/employee-advances")]
[Authorize]
[RequireFeatureOrSuperAdmin("PayrollAndPnL")]
public class EmployeeAdvancesController : ControllerBase
{
    private readonly AppDbContext _db;

    public EmployeeAdvancesController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<ActionResult<List<EmployeeAdvanceDto>>> List(
        [FromQuery] int? employeeId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var q = _db.EmployeeAdvances.AsQueryable();
        if (employeeId.HasValue) q = q.Where(a => a.EmployeeId == employeeId.Value);
        if (from.HasValue)       q = q.Where(a => a.Date >= from.Value.Date);
        if (to.HasValue)         q = q.Where(a => a.Date <  to.Value.Date.AddDays(1));

        var rows = await q.OrderByDescending(a => a.Date).ToListAsync();
        return rows.Select(Map).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<EmployeeAdvanceDto>> Create(CreateEmployeeAdvanceDto dto)
    {
        var emp = await _db.Employees.FindAsync(dto.EmployeeId);
        if (emp == null) return BadRequest("Zamestnanec neexistuje.");
        if (dto.Amount <= 0) return BadRequest("Suma musí byť kladná.");

        var adv = new EmployeeAdvance
        {
            EmployeeId = dto.EmployeeId,
            Date       = dto.Date.Date,
            Amount     = dto.Amount,
            Note       = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim(),
            CreatedBy  = User.Identity?.Name
        };
        _db.EmployeeAdvances.Add(adv);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), Map(adv));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<EmployeeAdvanceDto>> Update(int id, UpdateEmployeeAdvanceDto dto)
    {
        var adv = await _db.EmployeeAdvances.FindAsync(id);
        if (adv == null) return NotFound();
        if (dto.Amount <= 0) return BadRequest("Suma musí byť kladná.");

        adv.Date      = dto.Date.Date;
        adv.Amount    = dto.Amount;
        adv.Note      = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim();
        adv.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Map(adv);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var adv = await _db.EmployeeAdvances.FindAsync(id);
        if (adv == null) return NotFound();
        _db.EmployeeAdvances.Remove(adv);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static EmployeeAdvanceDto Map(EmployeeAdvance a) => new()
    {
        Id         = a.Id,
        EmployeeId = a.EmployeeId,
        Date       = a.Date,
        Amount     = a.Amount,
        Note       = a.Note,
        CreatedBy  = a.CreatedBy,
        CreatedAt  = a.CreatedAt
    };
}
