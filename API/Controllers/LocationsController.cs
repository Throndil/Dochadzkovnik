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
    private readonly IMaterialExcelExportService _excelExport;

    public LocationsController(
        AppDbContext db,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        IMaterialExcelExportService excelExport,
        IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _excelExport = excelExport;
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

    // ─────────────────────────────────────────────────────────────────────────
    //  Materials (consumption per location)
    // ─────────────────────────────────────────────────────────────────────────

    private (DateTime? from, DateTime? toExclusive) ParseDateRange(string? from, string? to)
    {
        DateTime? f = null, t = null;
        if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from, out var fd)) f = fd.Date;
        if (!string.IsNullOrEmpty(to)   && DateTime.TryParse(to,   out var td)) t = td.Date.AddDays(1); // inclusive on the "to" end
        return (f, t);
    }

    // GET /api/locations/{id}/materials?from=YYYY-MM-DD&to=YYYY-MM-DD
    [HttpGet("{id}/materials")]
    public async Task<ActionResult<List<MaterialUsageDto>>> GetMaterialUsages(int id, [FromQuery] string? from, [FromQuery] string? to)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        var (f, t) = ParseDateRange(from, to);

        var q = _db.MaterialUsages
            .Include(u => u.Material)
            .Include(u => u.Employee)
            .Where(u => u.LocationId == id);
        if (f.HasValue) q = q.Where(u => u.Date >= f.Value);
        if (t.HasValue) q = q.Where(u => u.Date <  t.Value);

        return await q
            .OrderByDescending(u => u.Date)
            .ThenByDescending(u => u.Id)
            .Select(u => new MaterialUsageDto
            {
                Id              = u.Id,
                LocationId      = u.LocationId,
                MaterialId      = u.MaterialId,
                MaterialName    = u.Material.Name,
                Unit            = u.Material.Unit,
                Quantity        = u.Quantity,
                UnitPriceAtTime = u.UnitPriceAtTime,
                LineCost        = u.Quantity * u.UnitPriceAtTime,
                Date            = u.Date,
                EmployeeId      = u.EmployeeId,
                EmployeeName    = u.Employee != null ? (u.Employee.FirstName + " " + u.Employee.LastName) : null,
                Note            = u.Note,
                PhotoUrl        = u.PhotoUrl
            })
            .ToListAsync();
    }

    // GET /api/locations/{id}/materials/summary?from=YYYY-MM-DD&to=YYYY-MM-DD
    [HttpGet("{id}/materials/summary")]
    public async Task<ActionResult<List<MaterialSummaryRowDto>>> GetMaterialSummary(int id, [FromQuery] string? from, [FromQuery] string? to)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        var (f, t) = ParseDateRange(from, to);

        var q = _db.MaterialUsages
            .Include(u => u.Material)
            .Where(u => u.LocationId == id);
        if (f.HasValue) q = q.Where(u => u.Date >= f.Value);
        if (t.HasValue) q = q.Where(u => u.Date <  t.Value);

        return await q
            .GroupBy(u => new { u.MaterialId, u.Material.Name, u.Material.Unit })
            .Select(g => new MaterialSummaryRowDto
            {
                MaterialId    = g.Key.MaterialId,
                MaterialName  = g.Key.Name,
                Unit          = g.Key.Unit,
                TotalQuantity = g.Sum(x => x.Quantity),
                // Use snapshot price (UnitPriceAtTime) — inflation-protected
                TotalCost     = g.Sum(x => x.Quantity * x.UnitPriceAtTime),
                EntryCount    = g.Count(),
                LastEntryDate = g.Max(x => (DateTime?)x.Date)
            })
            .OrderBy(r => r.MaterialName)
            .ToListAsync();
    }

    // POST /api/locations/{id}/materials
    [HttpPost("{id}/materials")]
    public async Task<ActionResult<MaterialUsageDto>> CreateMaterialUsage(int id, CreateMaterialUsageDto dto)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound("Pracovisko nebolo nájdené.");

        var material = await _db.Materials.FindAsync(dto.MaterialId);
        if (material == null) return BadRequest("Vybraný materiál neexistuje.");
        if (!material.IsActive) return BadRequest("Vybraný materiál je neaktívny.");

        if (dto.EmployeeId.HasValue)
        {
            var empExists = await _db.Employees.AnyAsync(e => e.Id == dto.EmployeeId.Value);
            if (!empExists) return BadRequest("Zamestnanec neexistuje.");
        }

        var usage = new MaterialUsage
        {
            LocationId      = id,
            MaterialId      = dto.MaterialId,
            EmployeeId      = dto.EmployeeId,
            Quantity        = dto.Quantity,
            // Snapshot the catalogue price NOW so future inflation/price changes don't
            // rewrite history. Caller may override if they're recording a backdated entry
            // and know the historical price.
            UnitPriceAtTime = dto.UnitPriceAtTime ?? material.PricePerUnit,
            Date            = dto.Date.Date, // strip time-of-day; we treat material entries as date-only
            Note            = dto.Note
        };

        _db.MaterialUsages.Add(usage);
        await _db.SaveChangesAsync();

        // Reload with includes for the response
        var saved = await _db.MaterialUsages
            .Include(u => u.Material)
            .Include(u => u.Employee)
            .FirstAsync(u => u.Id == usage.Id);

        return Ok(new MaterialUsageDto
        {
            Id              = saved.Id,
            LocationId      = saved.LocationId,
            MaterialId      = saved.MaterialId,
            MaterialName    = saved.Material.Name,
            Unit            = saved.Material.Unit,
            Quantity        = saved.Quantity,
            UnitPriceAtTime = saved.UnitPriceAtTime,
            LineCost        = saved.Quantity * saved.UnitPriceAtTime,
            Date            = saved.Date,
            EmployeeId      = saved.EmployeeId,
            EmployeeName    = saved.Employee != null ? (saved.Employee.FirstName + " " + saved.Employee.LastName) : null,
            Note            = saved.Note,
            PhotoUrl        = saved.PhotoUrl
        });
    }

    // PUT /api/locations/{id}/materials/{usageId}
    [HttpPut("{id}/materials/{usageId}")]
    public async Task<ActionResult> UpdateMaterialUsage(int id, int usageId, UpdateMaterialUsageDto dto)
    {
        var usage = await _db.MaterialUsages.FirstOrDefaultAsync(u => u.Id == usageId && u.LocationId == id);
        if (usage == null) return NotFound();

        var material = await _db.Materials.FindAsync(dto.MaterialId);
        if (material == null) return BadRequest("Vybraný materiál neexistuje.");

        // If the caller switched the entry to a DIFFERENT material, take a fresh snapshot
        // from the new material's current price (since the previous snapshot was for a
        // different commodity). Otherwise keep the original snapshot — that's the whole
        // point of inflation protection. An explicit dto.UnitPriceAtTime always wins.
        var materialChanged = usage.MaterialId != dto.MaterialId;
        usage.MaterialId = dto.MaterialId;
        usage.Quantity   = dto.Quantity;
        usage.Date       = dto.Date.Date;
        usage.EmployeeId = dto.EmployeeId;
        usage.Note       = dto.Note;
        if (dto.UnitPriceAtTime.HasValue)
            usage.UnitPriceAtTime = dto.UnitPriceAtTime.Value;
        else if (materialChanged)
            usage.UnitPriceAtTime = material.PricePerUnit;
        // else: leave the snapshot alone — this is the inflation-protection path

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // DELETE /api/locations/{id}/materials/{usageId}
    [HttpDelete("{id}/materials/{usageId}")]
    public async Task<ActionResult> DeleteMaterialUsage(int id, int usageId)
    {
        var usage = await _db.MaterialUsages.FirstOrDefaultAsync(u => u.Id == usageId && u.LocationId == id);
        if (usage == null) return NotFound();

        if (!string.IsNullOrEmpty(usage.PhotoUrl) && _blobStorage != null)
        {
            try { await _blobStorage.DeleteAsync(usage.PhotoUrl, "material-photos"); } catch { /* best-effort */ }
        }

        _db.MaterialUsages.Remove(usage);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // POST /api/locations/{id}/materials/{usageId}/photo  (multipart)
    [HttpPost("{id}/materials/{usageId}/photo")]
    public async Task<ActionResult<string>> UploadMaterialPhoto(int id, int usageId, IFormFile file)
    {
        var usage = await _db.MaterialUsages.FirstOrDefaultAsync(u => u.Id == usageId && u.LocationId == id);
        if (usage == null) return NotFound();

        if (file == null || file.Length == 0 || file.Length > 10 * 1024 * 1024)
            return BadRequest("Súbor musí byť medzi 1 B a 10 MB.");
        if (_blobStorage == null)
            return StatusCode(503, "Úložisko fotografií nie je nakonfigurované.");

        if (!string.IsNullOrEmpty(usage.PhotoUrl))
            try { await _blobStorage.DeleteAsync(usage.PhotoUrl, "material-photos"); } catch { }

        using var stream = file.OpenReadStream();
        var folder = $"material-photos/{id}/{usage.Date:yyyy-MM}";
        usage.PhotoUrl = await _blobStorage.UploadAsync(stream, file.FileName, folder);
        await _db.SaveChangesAsync();

        return Ok(usage.PhotoUrl);
    }

    // DELETE /api/locations/{id}/materials/{usageId}/photo
    [HttpDelete("{id}/materials/{usageId}/photo")]
    public async Task<ActionResult> DeleteMaterialPhoto(int id, int usageId)
    {
        var usage = await _db.MaterialUsages.FirstOrDefaultAsync(u => u.Id == usageId && u.LocationId == id);
        if (usage == null) return NotFound();
        if (string.IsNullOrEmpty(usage.PhotoUrl)) return NoContent();

        if (_blobStorage != null)
            try { await _blobStorage.DeleteAsync(usage.PhotoUrl, "material-photos"); } catch { }
        usage.PhotoUrl = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/locations/{id}/materials/export?from=YYYY-MM-DD&to=YYYY-MM-DD
    [HttpGet("{id}/materials/export")]
    public async Task<ActionResult> ExportMaterialsExcel(int id, [FromQuery] string? from, [FromQuery] string? to)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        var (f, t) = ParseDateRange(from, to);

        var entriesQuery = _db.MaterialUsages
            .Include(u => u.Material)
            .Include(u => u.Employee)
            .Where(u => u.LocationId == id);
        if (f.HasValue) entriesQuery = entriesQuery.Where(u => u.Date >= f.Value);
        if (t.HasValue) entriesQuery = entriesQuery.Where(u => u.Date <  t.Value);

        var entries = await entriesQuery
            .OrderByDescending(u => u.Date)
            .Select(u => new MaterialUsageDto
            {
                Id              = u.Id,
                LocationId      = u.LocationId,
                MaterialId      = u.MaterialId,
                MaterialName    = u.Material.Name,
                Unit            = u.Material.Unit,
                Quantity        = u.Quantity,
                UnitPriceAtTime = u.UnitPriceAtTime,
                LineCost        = u.Quantity * u.UnitPriceAtTime,
                Date            = u.Date,
                EmployeeId      = u.EmployeeId,
                EmployeeName    = u.Employee != null ? (u.Employee.FirstName + " " + u.Employee.LastName) : null,
                Note            = u.Note,
                PhotoUrl         = u.PhotoUrl
            })
            .ToListAsync();

        var summary = entries
            .GroupBy(e => new { e.MaterialId, e.MaterialName, e.Unit })
            .Select(g => new MaterialSummaryRowDto
            {
                MaterialId    = g.Key.MaterialId,
                MaterialName  = g.Key.MaterialName,
                Unit          = g.Key.Unit,
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalCost     = g.Sum(x => x.LineCost),
                EntryCount    = g.Count(),
                LastEntryDate = g.Max(x => (DateTime?)x.Date)
            })
            .OrderBy(r => r.MaterialName)
            .ToList();

        var bytes = _excelExport.BuildLocationMaterialReport(loc.Name, f, t.HasValue ? t.Value.AddDays(-1) : (DateTime?)null, summary, entries);

        // Sanitise filename — strip diacritics and spaces
        var safeName = string.Concat(
            loc.Name
                .Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                            != System.Globalization.UnicodeCategory.NonSpacingMark)
        ).Replace(' ', '_');

        var rangeTag = f.HasValue && t.HasValue
            ? $"{f.Value:yyyy-MM-dd}_{t.Value.AddDays(-1):yyyy-MM-dd}"
            : DateTime.UtcNow.ToString("yyyy-MM-dd");
        var fileName = $"Spotreba_{safeName}_{rangeTag}.xlsx";

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}
