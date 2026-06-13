using API.Data;
using API.DTOs;
using API.Filters;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// Admin surface for stavebný denník (construction diary) entries — see
/// PROOF_OF_WORK_UX_PLAN.md. JWT-protected; the kiosk side lives in
/// <see cref="KioskWorkDiariesController"/>. Both behind the
/// ProofOfWorkChoices feature flag.
/// </summary>
[ApiController]
[Route("api/work-diaries")]
[Authorize]
[RequireFeatureOrSuperAdmin("ProofOfWorkChoices")]
public class WorkDiariesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blobStorage;

    private const string DiaryFolderRoot = "work-diaries";

    public WorkDiariesController(AppDbContext db, IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
    }

    [HttpGet]
    public async Task<ActionResult<List<WorkDiaryDto>>> List(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? locationId,
        [FromQuery] int? employeeId)
    {
        var q = _db.WorkDiaries
            .Include(d => d.Employee)
            .Include(d => d.Location)
            .AsQueryable();

        if (from.HasValue)       q = q.Where(d => d.Date >= from.Value.Date);
        if (to.HasValue)         q = q.Where(d => d.Date <  to.Value.Date.AddDays(1));
        if (locationId.HasValue) q = q.Where(d => d.LocationId == locationId.Value);
        if (employeeId.HasValue) q = q.Where(d => d.EmployeeId == employeeId.Value);

        var rows = await q.OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).ToListAsync();
        return rows.Select(ToDto).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<WorkDiaryDto>> Get(int id)
    {
        var diary = await _db.WorkDiaries
            .Include(d => d.Employee)
            .Include(d => d.Location)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (diary == null) return NotFound();
        return ToDto(diary);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<WorkDiaryDto>> Update(int id, [FromBody] UpdateWorkDiaryDto dto)
    {
        var diary = await _db.WorkDiaries
            .Include(d => d.Employee)
            .Include(d => d.Location)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (diary == null) return NotFound();

        if (dto.Date.HasValue) diary.Date = dto.Date.Value.Date;
        if (dto.BodyText != null)
        {
            var trimmed = dto.BodyText.Trim();
            if (trimmed.Length == 0) return BadRequest("Text denníka nemôže byť prázdny.");
            diary.BodyText = trimmed;
        }

        await _db.SaveChangesAsync();
        return ToDto(diary);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var diary = await _db.WorkDiaries.FindAsync(id);
        if (diary == null) return NotFound();

        if (!string.IsNullOrEmpty(diary.AttachmentUrl) && _blobStorage != null)
        {
            try { await _blobStorage.DeleteAsync(diary.AttachmentUrl, DiaryFolderRoot); } catch { }
        }

        _db.WorkDiaries.Remove(diary);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/attachment")]
    public async Task<ActionResult<string>> UploadAttachment(int id, IFormFile file)
    {
        var diary = await _db.WorkDiaries.FindAsync(id);
        if (diary == null) return NotFound();

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

    [HttpDelete("{id}/attachment")]
    public async Task<IActionResult> DeleteAttachment(int id)
    {
        var diary = await _db.WorkDiaries.FindAsync(id);
        if (diary == null) return NotFound();
        if (string.IsNullOrEmpty(diary.AttachmentUrl)) return NoContent();

        if (_blobStorage != null)
        {
            try { await _blobStorage.DeleteAsync(diary.AttachmentUrl, DiaryFolderRoot); } catch { }
        }
        diary.AttachmentUrl = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static WorkDiaryDto ToDto(WorkDiary d) => new()
    {
        Id            = d.Id,
        EmployeeId    = d.EmployeeId,
        EmployeeName  = d.Employee == null ? null : $"{d.Employee.FirstName} {d.Employee.LastName}",
        LocationId    = d.LocationId,
        LocationName  = d.Location?.Name,
        TimeEntryId   = d.TimeEntryId,
        Date          = d.Date,
        BodyText      = d.BodyText,
        AttachmentUrl = d.AttachmentUrl,
        CreatedAt     = d.CreatedAt,
        UpdatedAt     = d.UpdatedAt
    };
}
