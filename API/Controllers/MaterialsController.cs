using API.Data;
using API.DTOs;
using API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MaterialsController : ControllerBase
{
    private readonly AppDbContext _db;

    public MaterialsController(AppDbContext db)
    {
        _db = db;
    }

    private static MaterialDto ToDto(Material m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Unit = m.Unit,
        PricePerUnit = m.PricePerUnit,
        IsActive = m.IsActive
    };

    // GET /api/materials?activeOnly=true
    [HttpGet]
    public async Task<ActionResult<List<MaterialDto>>> GetAll([FromQuery] bool activeOnly = false)
    {
        var q = _db.Materials.AsQueryable();
        if (activeOnly) q = q.Where(m => m.IsActive);
        return await q
            .OrderBy(m => m.Name)
            .Select(m => new MaterialDto
            {
                Id = m.Id,
                Name = m.Name,
                Unit = m.Unit,
                PricePerUnit = m.PricePerUnit,
                IsActive = m.IsActive
            })
            .ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MaterialDto>> Get(int id)
    {
        var m = await _db.Materials.FindAsync(id);
        if (m == null) return NotFound();
        return ToDto(m);
    }

    [HttpPost]
    public async Task<ActionResult<MaterialDto>> Create(CreateMaterialDto dto)
    {
        // Reject duplicate names (case-insensitive) so the customer doesn't end up with
        // "Cement" and "cement" as two different catalogue entries.
        var nameTrim = dto.Name.Trim();
        var exists = await _db.Materials.AnyAsync(m => m.Name.ToLower() == nameTrim.ToLower());
        if (exists) return Conflict("Materiál s týmto názvom už existuje.");

        var material = new Material
        {
            Name = nameTrim,
            Unit = dto.Unit.Trim(),
            PricePerUnit = dto.PricePerUnit
        };
        _db.Materials.Add(material);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = material.Id }, ToDto(material));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateMaterialDto dto)
    {
        var m = await _db.Materials.FindAsync(id);
        if (m == null) return NotFound();

        var nameTrim = dto.Name.Trim();
        var exists = await _db.Materials.AnyAsync(x => x.Id != id && x.Name.ToLower() == nameTrim.ToLower());
        if (exists) return Conflict("Materiál s týmto názvom už existuje.");

        m.Name = nameTrim;
        m.Unit = dto.Unit.Trim();
        // Inflation-protected: changing PricePerUnit only affects FUTURE usages.
        // Existing MaterialUsage rows keep their UnitPriceAtTime snapshot.
        m.PricePerUnit = dto.PricePerUnit;
        m.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/toggle-active")]
    public async Task<ActionResult> ToggleActive(int id)
    {
        var m = await _db.Materials.FindAsync(id);
        if (m == null) return NotFound();
        m.IsActive = !m.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Soft delete = deactivate. Hard delete only allowed if no usage records reference it,
    // because MaterialUsage→Material has Restrict (we don't want to lose history silently).
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var m = await _db.Materials.FindAsync(id);
        if (m == null) return NotFound();

        var inUse = await _db.MaterialUsages.AnyAsync(u => u.MaterialId == id);
        if (inUse)
        {
            // Soft-delete (keep history) — flip IsActive off so it stops appearing in dropdowns.
            m.IsActive = false;
            await _db.SaveChangesAsync();
            return Ok(new { soft = true, message = "Materiál je použitý v záznamoch — deaktivovaný namiesto odstránenia." });
        }

        _db.Materials.Remove(m);
        await _db.SaveChangesAsync();
        return Ok(new { soft = false });
    }
}
