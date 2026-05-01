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
        //
        // Edge case observed in production (2026-05-01): if a previously-used material was
        // soft-deleted (DELETE on a Material that has Usages flips IsActive=false instead of
        // hard-deleting, so MaterialUsage history stays valid), a Create with the same name
        // would 409 even though the customer cannot see the soft-deleted row in the catalogue
        // listing UI. Result: "Materiál s týmto názvom už existuje" is shown next to an empty
        // catalogue table — confusing for the customer. Fix: when the only existing match is
        // inactive, resurrect it (flip IsActive back on, refresh Unit + PricePerUnit) instead
        // of returning a conflict. Existing MaterialUsage snapshots are inflation-protected
        // by their own UnitPriceAtTime column so updating the catalogue price here does not
        // touch history.
        var nameTrim = dto.Name.Trim();
        var unitTrim = dto.Unit.Trim();
        var nameLower = nameTrim.ToLower();
        var existing = await _db.Materials
            .FirstOrDefaultAsync(m => m.Name.ToLower() == nameLower);

        if (existing != null)
        {
            if (existing.IsActive)
            {
                return Conflict("Materiál s týmto názvom už existuje.");
            }

            existing.IsActive = true;
            existing.Unit = unitTrim;
            existing.PricePerUnit = dto.PricePerUnit;
            // Normalise stored Name to the trimmed input (handles legacy rows with stray
            // whitespace; keeps the canonical name visible in the catalogue).
            existing.Name = nameTrim;
            await _db.SaveChangesAsync();
            return Ok(ToDto(existing));
        }

        var material = new Material
        {
            Name = nameTrim,
            Unit = unitTrim,
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
