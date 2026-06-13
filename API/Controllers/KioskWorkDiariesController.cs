using API.Data;
using API.DTOs;
using API.Filters;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// Kiosk-side endpoints for the Stavebný denník (construction diary) flow —
/// see PROOF_OF_WORK_UX_PLAN.md. PIN-validated (no JWT). Behind the
/// ProofOfWorkChoices feature flag at the class level so the controller is
/// invisible (404 everywhere) until the superadmin flips the flag.
/// Sister controller: <see cref="WorkDiariesController"/> for the JWT admin surface.
/// </summary>
[ApiController]
[Route("api/kiosk/work-diaries")]
[RequireFeatureOrSuperAdmin("ProofOfWorkChoices")]
public class KioskWorkDiariesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPinHasher _pinHasher;
    private readonly IBlobStorageService? _blobStorage;

    private const string DiaryFolderRoot = "work-diaries";

    public KioskWorkDiariesController(
        AppDbContext db,
        IPinHasher pinHasher,
        IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _pinHasher = pinHasher;
        _blobStorage = blobStorage;
    }

    /// <summary>
    /// Create a diary entry. Optionally links to an existing TimeEntry (the
    /// in-šichta combined flow); when null, the diary stands alone.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<WorkDiaryDto>> Create([FromBody] CreateKioskWorkDiaryDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        var location = await _db.Locations.FindAsync(dto.LocationId);
        if (location == null || !location.IsActive) return BadRequest("Neplatné pracovisko.");

        if (dto.TimeEntryId.HasValue)
        {
            var te = await _db.TimeEntries.FindAsync(dto.TimeEntryId.Value);
            if (te == null) return BadRequest("Neplatný záznam dochádzky.");
            if (te.EmployeeId != employee.Id)
                return BadRequest("Záznam dochádzky nepatrí prihlásenému zamestnancovi.");
        }

        var diary = new WorkDiary
        {
            EmployeeId  = employee.Id,
            LocationId  = dto.LocationId,
            TimeEntryId = dto.TimeEntryId,
            Date        = dto.Date.Date,
            BodyText    = dto.BodyText.Trim()
        };

        _db.WorkDiaries.Add(diary);
        await _db.SaveChangesAsync();

        return ToDto(diary, employee, location);
    }

    /// <summary>
    /// Upload (or replace) the attachment for an existing diary entry.
    /// PIN-validated AND ownership-checked: only the worker who created the
    /// diary can attach a file to it. Admin can overwrite via the JWT endpoint.
    /// </summary>
    [HttpPost("{id}/attachment")]
    public async Task<ActionResult<string>> UploadAttachment(int id, IFormFile file, [FromForm] string pin)
    {
        var employee = await FindEmployeeByPin(pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        var diary = await _db.WorkDiaries.FindAsync(id);
        if (diary == null) return NotFound();
        if (diary.EmployeeId != employee.Id) return Forbid();

        if (file == null || file.Length == 0 || file.Length > 10 * 1024 * 1024)
            return BadRequest("Súbor musí byť medzi 1 B a 10 MB.");
        if (_blobStorage == null)
            return StatusCode(503, "Úložisko súborov nie je nakonfigurované.");

        if (!string.IsNullOrEmpty(diary.AttachmentUrl))
        {
            try { await _blobStorage.DeleteAsync(diary.AttachmentUrl, DiaryFolderRoot); } catch { }
        }

        using var stream = file.OpenReadStream();
        var folder = $"{DiaryFolderRoot}/{diary.Date:yyyy-MM}";
        diary.AttachmentUrl = await _blobStorage.UploadAsync(stream, file.FileName, folder);
        await _db.SaveChangesAsync();

        return Ok(diary.AttachmentUrl);
    }

    private async Task<Employee?> FindEmployeeByPin(string pin)
    {
        if (string.IsNullOrEmpty(pin)) return null;
        var actives = await _db.Employees.Where(e => e.IsActive).ToListAsync();
        return actives.FirstOrDefault(e => _pinHasher.Verify(e.Pin, pin));
    }

    private static WorkDiaryDto ToDto(WorkDiary d, Employee employee, Location location) => new()
    {
        Id            = d.Id,
        EmployeeId    = d.EmployeeId,
        EmployeeName  = $"{employee.FirstName} {employee.LastName}",
        LocationId    = d.LocationId,
        LocationName  = location.Name,
        TimeEntryId   = d.TimeEntryId,
        Date          = d.Date,
        BodyText      = d.BodyText,
        AttachmentUrl = d.AttachmentUrl,
        CreatedAt     = d.CreatedAt,
        UpdatedAt     = d.UpdatedAt
    };
}
