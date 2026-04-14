using System.IO.Compression;
using System.Security.Claims;
using API.Data;
using API.DTOs;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LocationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blobStorage;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;

    public LocationsController(AppDbContext db, IConfiguration config, IHttpClientFactory httpClientFactory, IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
        _config = config;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    public async Task<ActionResult<List<LocationDto>>> GetAll()
    {
        return await _db.Locations
            .Select(l => new LocationDto
            {
                Id = l.Id,
                Name = l.Name,
                Address = l.Address,
                PhotoUrl = l.PhotoUrl,
                IsActive = l.IsActive
            })
            .ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<LocationDto>> Get(int id)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        return new LocationDto
        {
            Id = loc.Id,
            Name = loc.Name,
            Address = loc.Address,
            PhotoUrl = loc.PhotoUrl,
            IsActive = loc.IsActive
        };
    }

    [HttpPost]
    public async Task<ActionResult<LocationDto>> Create(CreateLocationDto dto)
    {
        var location = new Location
        {
            Name = dto.Name,
            Address = dto.Address
        };

        _db.Locations.Add(location);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = location.Id }, new LocationDto
        {
            Id = location.Id,
            Name = location.Name,
            Address = location.Address,
            IsActive = location.IsActive
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateLocationDto dto)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        loc.Name = dto.Name;
        loc.Address = dto.Address;
        loc.IsActive = dto.IsActive;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        loc.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/toggle-active")]
    public async Task<ActionResult> ToggleActive(int id)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        loc.IsActive = !loc.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}/permanent")]
    public async Task<ActionResult> DeletePermanent(int id)
    {
        var loc = await _db.Locations
            .Include(l => l.TimeEntries)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (loc == null) return NotFound();

        if (!string.IsNullOrEmpty(loc.PhotoUrl) && _blobStorage != null)
        {
            await _blobStorage.DeleteAsync(loc.PhotoUrl, "location-photos");
        }

        _db.TimeEntries.RemoveRange(loc.TimeEntries);
        _db.Locations.Remove(loc);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/locations/{id}/photos/download?from=YYYY-MM&to=YYYY-MM
    [HttpGet("{id}/photos/download")]
    public async Task<ActionResult> DownloadPhotos(int id, [FromQuery] string? from, [FromQuery] string? to)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        // Build date range
        DateTime? fromDate = null, toDateEnd = null;
        if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from + "-01", out var fd))
            fromDate = fd;
        if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to + "-01", out var td))
            toDateEnd = td.AddMonths(1);

        // Collect photo URLs from TimeEntries (include employee name)
        var teQuery = _db.TimeEntries
            .Include(t => t.Employee)
            .Where(t => t.LocationId == id && t.PhotoUrl != null);
        if (fromDate.HasValue)   teQuery = teQuery.Where(t => t.ClockIn >= fromDate.Value);
        if (toDateEnd.HasValue)  teQuery = teQuery.Where(t => t.ClockIn < toDateEnd.Value);

        var teRaw = await teQuery
            .OrderBy(t => t.ClockIn)
            .Select(t => new
            {
                t.PhotoUrl,
                t.ClockIn,
                EmployeeName = t.Employee.FirstName + " " + t.Employee.LastName
            })
            .ToListAsync();

        // Expand comma-separated URLs into individual entries
        var tePhotos = teRaw
            .SelectMany(t => t.PhotoUrl!
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(url => new { PhotoUrl = url.Trim(), t.ClockIn, t.EmployeeName }))
            .ToList();

        // Collect photo URLs from standalone WorkPhotos (include employee name)
        var wpQuery = _db.WorkPhotos
            .Include(w => w.Employee)
            .Where(w => w.LocationId == id);
        if (fromDate.HasValue)  wpQuery = wpQuery.Where(w => w.CreatedAt >= fromDate.Value);
        if (toDateEnd.HasValue) wpQuery = wpQuery.Where(w => w.CreatedAt < toDateEnd.Value);

        var wpPhotos = await wpQuery
            .OrderBy(w => w.CreatedAt)
            .Select(w => new
            {
                PhotoUrl = w.PhotoUrl,
                ClockIn = w.CreatedAt,
                EmployeeName = w.Note != null && w.Note != ""
                    ? w.Note
                    : (w.Employee != null
                        ? w.Employee.FirstName + " " + w.Employee.LastName
                        : "Admin")
            })
            .ToListAsync();

        var allPhotos = tePhotos
            .Select(p => new { p.PhotoUrl, p.ClockIn, p.EmployeeName })
            .Concat(wpPhotos.Select(p => new { p.PhotoUrl, p.ClockIn, p.EmployeeName }))
            .OrderBy(p => p.ClockIn)
            .ToList();

        if (!allPhotos.Any())
            return NotFound("No photos found for the selected period");

        // Download each photo and pack into a ZIP streamed back to the client
        var http = _httpClientFactory.CreateClient();
        var zipMs = new MemoryStream();

        // Track per-day-person counters for unique filenames
        var nameCounters = new Dictionary<string, int>();

        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var p in allPhotos)
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(p.PhotoUrl!);
                    var ext = Path.GetExtension(new Uri(p.PhotoUrl!).AbsolutePath);
                    if (string.IsNullOrEmpty(ext)) ext = ".jpg";

                    // Sanitise employee name: remove diacritics-ish chars unsafe in filenames
                    var safeEmployee = string.Concat(
                        p.EmployeeName
                            .Normalize(System.Text.NormalizationForm.FormD)
                            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                                        != System.Globalization.UnicodeCategory.NonSpacingMark)
                    ).Replace(' ', '_');

                    var baseKey = $"{p.ClockIn:yyyy-MM-dd}_{safeEmployee}";
                    nameCounters.TryGetValue(baseKey, out var counter);
                    counter++;
                    nameCounters[baseKey] = counter;

                    var filename = counter == 1
                        ? $"{baseKey}{ext}"
                        : $"{baseKey}_{counter:D2}{ext}";

                    var entry = archive.CreateEntry(filename, CompressionLevel.Fastest);
                    await using var es = entry.Open();
                    await es.WriteAsync(bytes);
                }
                catch { /* skip unreachable photos */ }
            }
        }

        zipMs.Seek(0, SeekOrigin.Begin);
        var safeName = $"fotky-{loc.Name.Replace(" ", "_")}-{from ?? "all"}.zip";
        return File(zipMs, "application/zip", safeName);
    }

    // GET /api/locations/{id}/photos?from=YYYY-MM&to=YYYY-MM
    [HttpGet("{id}/photos")]
    public async Task<ActionResult<List<LocationPhotoDto>>> GetPhotos(int id, [FromQuery] string? from, [FromQuery] string? to)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        DateTime? fromDate = null, toDateEnd = null;
        if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from + "-01", out var fd)) fromDate = fd;
        if (!string.IsNullOrEmpty(to)   && DateTime.TryParse(to   + "-01", out var td)) toDateEnd = td.AddMonths(1);

        // Expand comma-separated PhotoUrl strings into individual LocationPhotoDto entries
        var rawEntryPhotos = await _db.TimeEntries
            .Include(t => t.Employee)
            .Where(t => t.LocationId == id && t.PhotoUrl != null
                        && (fromDate == null   || t.ClockIn >= fromDate)
                        && (toDateEnd == null  || t.ClockIn < toDateEnd))
            .Select(t => new
            {
                TimeEntryId  = t.Id,
                EmployeeName = t.Employee.FirstName + " " + t.Employee.LastName,
                Date         = t.ClockIn,
                PhotoUrl     = t.PhotoUrl!
            })
            .ToListAsync();

        var entryPhotos = rawEntryPhotos
            .SelectMany(t => t.PhotoUrl
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(url => new LocationPhotoDto
                {
                    TimeEntryId  = t.TimeEntryId,
                    WorkPhotoId  = null,
                    EmployeeName = t.EmployeeName,
                    Date         = t.Date,
                    PhotoUrl     = url.Trim()
                }))
            .ToList();

        // For admin-uploaded WorkPhotos, Note holds the real uploader name from the JWT.
        // Use Note when set; fall back to the employee name (covers worker-uploaded WorkPhotos).
        var workPhotos = await _db.WorkPhotos
            .Include(w => w.Employee)
            .Where(w => w.LocationId == id
                        && (fromDate == null  || w.CreatedAt >= fromDate)
                        && (toDateEnd == null || w.CreatedAt < toDateEnd))
            .Select(w => new LocationPhotoDto
            {
                TimeEntryId  = null,
                WorkPhotoId  = w.Id,
                EmployeeName = w.Note != null && w.Note != ""
                    ? w.Note
                    : (w.Employee != null
                        ? w.Employee.FirstName + " " + w.Employee.LastName
                        : "Admin"),
                Date         = w.CreatedAt,
                PhotoUrl     = w.PhotoUrl
            })
            .ToListAsync();

        var combined = entryPhotos
            .Concat(workPhotos)
            .OrderByDescending(p => p.Date)
            .ToList();

        return Ok(combined);
    }

    // DELETE /api/locations/{id}/photos?before=YYYY-MM-DD
    [HttpDelete("{id}/photos")]
    public async Task<ActionResult<int>> BulkDeletePhotos(int id, [FromQuery] string before)
    {
        if (!DateTime.TryParse(before, out var beforeDate))
            return BadRequest("Invalid date format");

        var entries = await _db.TimeEntries
            .Where(t => t.LocationId == id && t.PhotoUrl != null && t.ClockIn < beforeDate)
            .ToListAsync();

        if (_blobStorage != null)
        {
            foreach (var entry in entries)
            {
                // Handle comma-separated multi-photo URLs
                foreach (var url in entry.PhotoUrl!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    await _blobStorage.DeleteAsync(url.Trim(), "work-photos");
                entry.PhotoUrl = null;
            }
        }
        else
        {
            foreach (var entry in entries) entry.PhotoUrl = null;
        }

        var workPhotos = await _db.WorkPhotos
            .Where(w => w.LocationId == id && w.CreatedAt < beforeDate)
            .ToListAsync();

        if (_blobStorage != null)
        {
            foreach (var wp in workPhotos)
                await _blobStorage.DeleteAsync(wp.PhotoUrl, "work-photos");
        }
        _db.WorkPhotos.RemoveRange(workPhotos);

        await _db.SaveChangesAsync();
        return Ok(entries.Count + workPhotos.Count);
    }

    // POST /api/locations/{id}/photo  — updates the location's cover photo
    [HttpPost("{id}/photo")]
    public async Task<ActionResult<string>> UploadPhoto(int id, IFormFile file)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        if (file.Length == 0 || file.Length > 5 * 1024 * 1024)
            return BadRequest("File must be between 1 byte and 5MB");

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return BadRequest("Only image files (jpg, png, gif, webp) are allowed");

        if (_blobStorage == null)
            return StatusCode(503, "Photo storage is not configured");

        if (!string.IsNullOrEmpty(loc.PhotoUrl))
            await _blobStorage.DeleteAsync(loc.PhotoUrl, "location-photos");

        using var stream = file.OpenReadStream();
        loc.PhotoUrl = await _blobStorage.UploadAsync(stream, file.FileName, "location-photos");
        await _db.SaveChangesAsync();

        return Ok(loc.PhotoUrl);
    }

    // POST /api/locations/{id}/gallery-photo  — admin adds a photo directly to the gallery
    // Optional form field: takenAt (ISO date string, e.g. "2026-03-15") — when omitted, defaults to UtcNow
    [HttpPost("{id}/gallery-photo")]
    public async Task<ActionResult<string>> UploadGalleryPhoto(int id, IFormFile file, [FromForm] string? takenAt)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        if (_blobStorage == null)
            return StatusCode(503, "Photo storage is not configured");

        // Use admin-supplied date if valid; otherwise fall back to now
        DateTime photoDate = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(takenAt) && DateTime.TryParse(takenAt, out var parsedDate))
            photoDate = new DateTime(parsedDate.Year, parsedDate.Month, parsedDate.Day, 12, 0, 0, DateTimeKind.Utc);

        // Get the admin's display name from the JWT so the gallery shows who uploaded the photo
        var adminName = User.FindFirstValue("displayName")
                        ?? User.Identity?.Name
                        ?? "Admin";

        // Resolve the system admin Employee (seeded at startup) — needed to satisfy the FK.
        // IsActive = false so it never appears in the kiosk or employee lists.
        const string adminPin = "SYSTEM_ADMIN_GALLERY_UPLOADER";
        var adminEmployee = await _db.Employees.FirstOrDefaultAsync(e => e.Pin == adminPin);
        if (adminEmployee == null)
            return StatusCode(500, "System admin employee not found. Please restart the API.");

        var folder = $"work-photos/{id}/{photoDate:yyyy-MM}";
        using var stream = file.OpenReadStream();
        var photoUrl = await _blobStorage.UploadAsync(stream, file.FileName, folder);

        var workPhoto = new WorkPhoto
        {
            EmployeeId = adminEmployee.Id,
            LocationId = id,
            PhotoUrl   = photoUrl,
            Note       = adminName,   // real uploader name from JWT; shown in gallery as EmployeeName
            CreatedAt  = photoDate
        };

        _db.WorkPhotos.Add(workPhoto);
        await _db.SaveChangesAsync();

        return Ok(photoUrl);
    }
}
