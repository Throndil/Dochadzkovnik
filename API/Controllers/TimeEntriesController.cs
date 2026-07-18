using API.Data;
using API.DTOs;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/time-entries")]
[Authorize]
public class TimeEntriesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blob;

    public TimeEntriesController(AppDbContext db, IBlobStorageService? blob = null)
    {
        _db = db;
        _blob = blob;
    }

    [HttpGet]
    public async Task<ActionResult<List<TimeEntryDto>>> GetAll(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? employeeId,
        [FromQuery] int? locationId)
    {
        var query = _db.TimeEntries
            .Include(t => t.Employee)
            .Include(t => t.Location)
            .Include(t => t.Car)
            .Include(t => t.Machine)
            .AsQueryable();

        if (from.HasValue)
            query = query.Where(t => t.ClockIn >= from.Value.Date);

        if (to.HasValue)
            query = query.Where(t => t.ClockIn < to.Value.Date.AddDays(1));

        if (employeeId.HasValue)
            query = query.Where(t => t.EmployeeId == employeeId.Value);

        if (locationId.HasValue)
            query = query.Where(t => t.LocationId == locationId.Value);

        return await query
            .OrderByDescending(t => t.ClockIn)
            .Select(t => new TimeEntryDto
            {
                Id = t.Id,
                EmployeeId = t.EmployeeId,
                EmployeeName = t.Employee.FirstName + " " + t.Employee.LastName,
                EmployeePhotoUrl = t.Employee.PhotoUrl,
                LocationId = t.LocationId,
                LocationName = t.Location.Name,
                CarId = t.CarId,
                CarName = t.Car != null ? t.Car.Name : null,
                MachineId = t.MachineId,
                MachineName = t.Machine != null ? t.Machine.Name : null,
                ClockIn = t.ClockIn,
                ClockOut = t.ClockOut,
                HoursWorked = t.ClockOut.HasValue
                    ? (t.ClockOut.Value - t.ClockIn).TotalHours
                    : null,
                Note = t.Note,
                PhotoUrl = t.PhotoUrl,
                ProofOfWorkSkipped = t.ProofOfWorkSkipped,
                HasDiary = _db.WorkDiaries.Any(d => d.TimeEntryId == t.Id),
                DiaryBody = _db.WorkDiaries
                               .Where(d => d.TimeEntryId == t.Id)
                               .OrderBy(d => d.Id)
                               .Select(d => d.BodyText)
                               .FirstOrDefault()
            })
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<TimeEntryDto>> Create(CreateTimeEntryDto dto)
    {
        var employee = await _db.Employees.FindAsync(dto.EmployeeId);
        if (employee == null) return BadRequest("Employee not found");
        // The kiosk resolves PINs only among IsActive employees. Booking an
        // entry against an inactive employee would create a record the worker
        // can never see in "Moje hodiny", which is confusing and almost always
        // a mistake.
        if (!employee.IsActive) return BadRequest("Zamestnanec je neaktívny — aktivujte ho pred pridaním záznamu.");

        var location = await _db.Locations.FindAsync(dto.LocationId);
        if (location == null) return BadRequest("Location not found");

        var entry = new TimeEntry
        {
            EmployeeId = dto.EmployeeId,
            LocationId = dto.LocationId,
            CarId = dto.CarId,
            MachineId = dto.MachineId,
            ClockIn = dto.ClockIn,
            ClockOut = dto.ClockOut,
            Note = dto.Note,
            // Snapshot wage at insert (PAYROLL_AND_PNL_PLAN.md §(a)).
            WageAtTime = employee.HourlyWage ?? 0m
        };

        _db.TimeEntries.Add(entry);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new TimeEntryDto
        {
            Id = entry.Id,
            EmployeeId = entry.EmployeeId,
            EmployeeName = employee.FirstName + " " + employee.LastName,
            LocationId = entry.LocationId,
            LocationName = location.Name,
            ClockIn = entry.ClockIn,
            ClockOut = entry.ClockOut,
            HoursWorked = entry.ClockOut.HasValue
                ? (entry.ClockOut.Value - entry.ClockIn).TotalHours
                : null,
            Note = entry.Note,
            PhotoUrl = entry.PhotoUrl,
            ProofOfWorkSkipped = entry.ProofOfWorkSkipped,
            // Just-created TimeEntry can't yet have a linked diary — set false
            // explicitly rather than firing a needless EXISTS query.
            HasDiary = false,
            DiaryBody = null
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateTimeEntryDto dto)
    {
        var entry = await _db.TimeEntries.FindAsync(id);
        if (entry == null) return NotFound();

        entry.CarId = dto.CarId;
        entry.MachineId = dto.MachineId;
        entry.ClockIn = dto.ClockIn;
        entry.ClockOut = dto.ClockOut;
        entry.Note = dto.Note;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var entry = await _db.TimeEntries.FindAsync(id);
        if (entry == null) return NotFound();

        // Remove associated photos from Cloudinary (PhotoUrl may be comma-separated)
        if (!string.IsNullOrEmpty(entry.PhotoUrl) && _blob != null)
        {
            foreach (var photoUrl in entry.PhotoUrl.Split(',', StringSplitOptions.RemoveEmptyEntries))
                await _blob.DeleteAsync(photoUrl.Trim(), "work-photos");
        }

        _db.TimeEntries.Remove(entry);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // POST /api/time-entries/{id}/photo
    [HttpPost("{id}/photo")]
    public async Task<ActionResult<PhotoUploadResultDto>> UploadPhoto(int id, IFormFile photo)
    {
        var entry = await _db.TimeEntries
            .Include(t => t.Location)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (entry == null) return NotFound();

        if (_blob == null)
            return StatusCode(503, "Storage service not configured");

        if (photo == null || photo.Length == 0)
            return BadRequest("No file provided");

        if (photo.Length > 20 * 1024 * 1024)
            return BadRequest("File too large (max 20 MB)");

        var locationName = (await _db.Locations.FindAsync(entry.LocationId))?.Name;
        var folder = CloudinaryFolders.WorkPhotos(entry.LocationId, locationName, entry.ClockIn);

        await using var stream = photo.OpenReadStream();
        var url = await _blob.UploadAsync(stream, photo.FileName, folder);

        // Append to the comma-separated PhotoUrl list. To remove a specific photo,
        // use DELETE /api/time-entries/{id}/photo?url=...
        entry.PhotoUrl = string.IsNullOrEmpty(entry.PhotoUrl)
            ? url
            : entry.PhotoUrl + "," + url;
        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new PhotoUploadResultDto { PhotoUrl = url });
    }

    // DELETE /api/time-entries/{id}/photo?url=<encodedUrl>
    // If url is provided, removes only that specific photo from the comma-separated list.
    // If url is omitted, removes ALL photos for the entry (legacy behaviour).
    [HttpDelete("{id}/photo")]
    public async Task<ActionResult> DeletePhoto(int id, [FromQuery] string? url)
    {
        var entry = await _db.TimeEntries.FindAsync(id);
        if (entry == null) return NotFound();

        if (string.IsNullOrEmpty(entry.PhotoUrl))
            return NoContent();

        if (!string.IsNullOrEmpty(url))
        {
            // Remove only the specified URL from the comma-separated list
            var remaining = entry.PhotoUrl
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => u != url.Trim())
                .ToList();

            if (_blob != null)
                await _blob.DeleteAsync(url.Trim(), "work-photos");

            entry.PhotoUrl = remaining.Count > 0 ? string.Join(",", remaining) : null;
        }
        else
        {
            // Delete all photos
            if (_blob != null)
            {
                foreach (var u in entry.PhotoUrl.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    await _blob.DeleteAsync(u.Trim(), "work-photos");
            }
            entry.PhotoUrl = null;
        }

        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return NoContent();
    }
}
