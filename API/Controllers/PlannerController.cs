using API.Data;
using API.Filters;
using API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace API.Controllers;

/// <summary>
/// Plánovač (behind the "Planner" flag) — CRUD for <see cref="PlanEntry"/>
/// bars. The week grid on /admin/planner is the only consumer. Manager-only;
/// nothing here is exposed to the kiosk.
/// </summary>
[ApiController]
[Route("api/planner")]
[Authorize]
[RequireFeatureOrSuperAdmin("Planner")]
public class PlannerController : ControllerBase
{
    private static readonly string[] Types = { "praca", "dovolenka", "pn", "volno" };

    private readonly AppDbContext _db;

    public PlannerController(AppDbContext db) => _db = db;

    private static PlanEntryDto ToDto(PlanEntry e) => new()
    {
        Id = e.Id,
        EmployeeId = e.EmployeeId,
        Type = e.Type,
        LocationId = e.LocationId,
        LocationName = e.Location?.Name,
        StartDate = e.StartDate,
        EndDate = e.EndDate,
        Note = e.Note
    };

    // GET /api/planner?from=YYYY-MM-DD&to=YYYY-MM-DD — bars overlapping the range.
    [HttpGet]
    public async Task<ActionResult<List<PlanEntryDto>>> List([FromQuery] string? from, [FromQuery] string? to)
    {
        if (!TryParseDay(from, out var f) || !TryParseDay(to, out var t)
            || f == null || t == null || f > t)
            return BadRequest("Neplatný rozsah dátumov.");

        var entries = await _db.PlanEntries
            .Include(e => e.Location)
            .Where(e => e.StartDate <= t && e.EndDate >= f)
            .OrderBy(e => e.StartDate)
            .ToListAsync();
        return entries.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<PlanEntryDto>> Create([FromBody] SavePlanEntryDto dto)
    {
        var entry = new PlanEntry { CreatedBy = User.Identity?.Name };
        var err = await ApplyAsync(entry, dto);
        if (err != null) return BadRequest(err);
        _db.PlanEntries.Add(entry);
        await _db.SaveChangesAsync();
        return ToDto(entry);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PlanEntryDto>> Update(int id, [FromBody] SavePlanEntryDto dto)
    {
        var entry = await _db.PlanEntries.Include(e => e.Location).FirstOrDefaultAsync(e => e.Id == id);
        if (entry == null) return NotFound();
        var err = await ApplyAsync(entry, dto);
        if (err != null) return BadRequest(err);
        entry.CreatedBy = User.Identity?.Name;
        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return ToDto(entry);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var entry = await _db.PlanEntries.FindAsync(id);
        if (entry == null) return NotFound();
        _db.PlanEntries.Remove(entry);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<string?> ApplyAsync(PlanEntry entry, SavePlanEntryDto dto)
    {
        if (!Types.Contains(dto.Type)) return "Neplatný typ.";
        if (!TryParseDay(dto.StartDate, out var start) || !TryParseDay(dto.EndDate, out var end))
            return "Neplatný dátum.";
        if (start == null || end == null) return "Dátum od aj do je povinný.";
        if (start > end) return "Dátum od je po dátume do.";
        if ((end.Value - start.Value).TotalDays > 366) return "Rozsah je príliš dlhý.";

        if (await _db.Employees.FindAsync(dto.EmployeeId) == null)
            return "Zamestnanec neexistuje.";

        if (dto.Type == "praca")
        {
            var loc = dto.LocationId is > 0 ? await _db.Locations.FindAsync(dto.LocationId.Value) : null;
            if (loc == null) return "Vyberte pracovisko.";
            entry.LocationId = loc.Id;
            entry.Location = loc;
        }
        else
        {
            entry.LocationId = null;
            entry.Location = null;
        }

        entry.EmployeeId = dto.EmployeeId;
        entry.Type = dto.Type;
        entry.StartDate = start.Value;
        entry.EndDate = end.Value;
        entry.Note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim();
        return null;
    }

    private static bool TryParseDay(string? value, out DateTime? day)
    {
        day = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return false;
        day = d.Date;
        return true;
    }

    public sealed class PlanEntryDto
    {
        public int Id { get; set; }
        public int EmployeeId { get; set; }
        public string Type { get; set; } = "praca";
        public int? LocationId { get; set; }
        public string? LocationName { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string? Note { get; set; }
    }

    public sealed class SavePlanEntryDto
    {
        public int EmployeeId { get; set; }
        [Required] public string Type { get; set; } = "praca";
        public int? LocationId { get; set; }
        [Required] public string StartDate { get; set; } = string.Empty;
        [Required] public string EndDate { get; set; } = string.Empty;
        [StringLength(500)] public string? Note { get; set; }
    }
}
