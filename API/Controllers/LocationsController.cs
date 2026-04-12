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

    public LocationsController(AppDbContext db, IConfiguration config, IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
        _config = config;
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

        var query = _db.TimeEntries
            .Where(t => t.LocationId == id && t.PhotoUrl != null);

        if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from + "-01", out var fromDate))
            query = query.Where(t => t.ClockIn >= fromDate);

        if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to + "-01", out var toDate))
        {
            var toDateEnd = toDate.AddMonths(1);
            query = query.Where(t => t.ClockIn < toDateEnd);
        }

        var photoUrls = await query.Select(t => t.PhotoUrl!).ToListAsync();
        if (!photoUrls.Any()) return NotFound("No photos found for the selected period");

        var publicIds = photoUrls
            .Select(url =>
            {
                try
                {
                    var uri = new Uri(url);
                    var path = uri.AbsolutePath;
                    var uploadIdx = path.IndexOf("/upload/", StringComparison.Ordinal);
                    if (uploadIdx < 0) return null;
                    var afterUpload = path[(uploadIdx + 8)..];
                    var slashIdx = afterUpload.IndexOf('/');
                    if (slashIdx < 0) return null;
                    var withExt = afterUpload[(slashIdx + 1)..];
                    var dir = Path.GetDirectoryName(withExt)?.Replace('\\', '/');
                    var name = Path.GetFileNameWithoutExtension(withExt);
                    return string.IsNullOrEmpty(dir) ? name : $"{dir}/{name}";
                }
                catch { return null; }
            })
            .Where(id2 => id2 != null)
            .Select(id2 => id2!)
            .ToList();

        if (!publicIds.Any()) return StatusCode(503, "Could not extract Cloudinary public IDs");

        var cloudName = _config["Cloudinary:CloudName"];
        if (string.IsNullOrEmpty(cloudName)) return StatusCode(503, "Cloudinary not configured");

        var ids = string.Join(",", publicIds.Select(p => Uri.EscapeDataString(p)));
        var zipUrl = $"https://api.cloudinary.com/v1_1/{cloudName}/image/download?public_ids={ids}&prefixes=work-photos/{id}";

        return Redirect(zipUrl);
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

        var entryPhotos = await _db.TimeEntries
            .Include(t => t.Employee)
            .Where(t => t.LocationId == id && t.PhotoUrl != null
                        && (fromDate == null   || t.ClockIn >= fromDate)
                        && (toDateEnd == null  || t.ClockIn < toDateEnd))
            .Select(t => new LocationPhotoDto
            {
                TimeEntryId  = t.Id,
                WorkPhotoId  = null,
                EmployeeName = t.Employee.FirstName + " " + t.Employee.LastName,
                Date         = t.ClockIn,
                PhotoUrl     = t.PhotoUrl!
            })
            .ToListAsync();

        // Includes admin-uploaded photos where EmployeeId is null
        var workPhotos = await _db.WorkPhotos
            .Include(w => w.Employee)
            .Where(w => w.LocationId == id
                        && (fromDate == null  || w.CreatedAt >= fromDate)
                        && (toDateEnd == null || w.CreatedAt < toDateEnd))
            .Select(w => new LocationPhotoDto
            {
                TimeEntryId  = null,
                WorkPhotoId  = w.Id,
                EmployeeName = w.Employee != null
                    ? w.Employee.FirstName + " " + w.Employee.LastName
                    : "Admin",
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
                await _blobStorage.DeleteAsync(entry.PhotoUrl!, "work-photos");
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
    [HttpPost("{id}/gallery-photo")]
    public async Task<ActionResult<string>> UploadGalleryPhoto(int id, IFormFile file)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        if (file == null || file.Length == 0)
            return BadRequest("No file provided");

        if (_blobStorage == null)
            return StatusCode(503, "Photo storage is not configured");

        var folder = $"work-photos/{id}/{DateTime.UtcNow:yyyy-MM}";
        using var stream = file.OpenReadStream();
        var photoUrl = await _blobStorage.UploadAsync(stream, file.FileName, folder);

        var workPhoto = new WorkPhoto
        {
            EmployeeId = null,
            LocationId = id,
            PhotoUrl   = photoUrl,
            Note       = "Admin upload",
            CreatedAt  = DateTime.UtcNow
        };

        _db.WorkPhotos.Add(workPhoto);
        await _db.SaveChangesAsync();

        return Ok(photoUrl);
    }
}
