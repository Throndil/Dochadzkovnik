using System.IO.Compression;
using System.Security.Claims;
using API.Data;
using API.DTOs;
using API.Filters;
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
    private readonly IPayrollExcelExportService _payrollExcelExport;

    public LocationsController(
        AppDbContext db,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        IMaterialExcelExportService excelExport,
        IPayrollExcelExportService payrollExcelExport,
        IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _excelExport = excelExport;
        _payrollExcelExport = payrollExcelExport;
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

        var locationName = (await _db.Locations.FindAsync(id))?.Name;
        var folder = CloudinaryFolders.WorkPhotos(id, locationName, photoDate);
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
        var combined = await BuildUnifiedMaterialEntriesAsync(id, f, t);
        return combined;
    }

    // GET /api/locations/{id}/materials/summary?from=YYYY-MM-DD&to=YYYY-MM-DD
    [HttpGet("{id}/materials/summary")]
    public async Task<ActionResult<List<MaterialSummaryRowDto>>> GetMaterialSummary(int id, [FromQuery] string? from, [FromQuery] string? to)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        var (f, t) = ParseDateRange(from, to);
        var combined = await BuildUnifiedMaterialEntriesAsync(id, f, t);

        return combined
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
    }

    /// <summary>
    /// Builds the unified per-location material entries list — real <c>MaterialUsage</c>
    /// rows AND read-side syntheses from <c>MaterialPurchaseLine</c> rows that target
    /// this location and have a linked <c>MaterialId</c> (post-promotion). Free-typed
    /// orphan lines (MaterialId == null) are excluded by design; promoting them in the
    /// admin Neidentifikované tab makes them appear here automatically.
    ///
    /// Synthetic rows carry FromPurchase=true and PurchaseId; the slide-over panel
    /// disables per-row edit/delete on those, sending the admin to the Nákupy tab
    /// for any changes.
    /// </summary>
    private async Task<List<MaterialUsageDto>> BuildUnifiedMaterialEntriesAsync(int locationId, DateTime? f, DateTime? t)
    {
        // ── Real MaterialUsage rows ──
        var uq = _db.MaterialUsages
            .Include(u => u.Material)
            .Include(u => u.Employee)
            .Where(u => u.LocationId == locationId);
        if (f.HasValue) uq = uq.Where(u => u.Date >= f.Value);
        if (t.HasValue) uq = uq.Where(u => u.Date <  t.Value);

        var usages = await uq.ToListAsync();

        // Lines that are already represented by a real MaterialUsage (Option A
        // flow: an invoice was committed and minted usages back-pointing to
        // their MaterialPurchaseLines). The pseudo-row builder below skips
        // these so we don't show the same physical purchase twice.
        var consumedLineIds = usages
            .Where(u => u.SourceMaterialPurchaseLineId.HasValue)
            .Select(u => u.SourceMaterialPurchaseLineId!.Value)
            .ToHashSet();

        var usageRows = usages.Select(u => new MaterialUsageDto
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
            PhotoUrl        = u.PhotoUrl,
            FromPurchase    = false,
            PurchaseId      = null,
            IsService       = u.IsService
        }).ToList();

        // ── Synthesised rows from MaterialPurchase lines targeting this location ──
        // The MaterialId filter (l.MaterialId != null) means promoted lines appear here
        // automatically — admin merges "Cemnt" into Cement and the merged line shows up
        // under the canonical material from then on.
        // A line belongs to its EFFECTIVE site: its own LocationId override when
        // set, otherwise the delivery list's. So pull purchases that target this
        // site OR have any line individually assigned to it, then filter per line.
        var pq = _db.MaterialPurchases
            .Include(p => p.Employee)
            .Include(p => p.Lines).ThenInclude(l => l.Material)
            .Where(p => p.LocationId == locationId || p.Lines.Any(l => l.LocationId == locationId));
        if (f.HasValue) pq = pq.Where(p => p.PurchaseDate >= f.Value);
        if (t.HasValue) pq = pq.Where(p => p.PurchaseDate <  t.Value);

        var purchases = await pq.ToListAsync();

        var purchaseRows = purchases.SelectMany(p => p.Lines
            .Where(l => (l.LocationId ?? p.LocationId) == locationId)
            .Where(l => l.MaterialId != null && l.Material != null)
            .Where(l => !consumedLineIds.Contains(l.Id))
            .Select(l => new MaterialUsageDto
            {
                // Negate the line id so synthetic ids never collide with real ones.
                // UI uses FromPurchase, not the sign of Id, but disjoint ids make
                // *track-by* loops behave even when the same value would otherwise
                // appear twice (which it never should in practice — but safer).
                Id              = -l.Id,
                LocationId      = locationId,
                MaterialId      = l.MaterialId!.Value,
                MaterialName    = l.Material!.Name,
                Unit            = l.Material.Unit,
                Quantity        = l.Quantity,
                // Snapshot the price the worker actually paid at purchase time.
                UnitPriceAtTime = l.UnitPrice,
                LineCost        = l.LineTotal,
                // Use the purchase date — that's when the material entered the site.
                Date            = p.PurchaseDate.Date,
                EmployeeId      = p.EmployeeId,
                EmployeeName    = $"{p.Employee.FirstName} {p.Employee.LastName}",
                // Surface a hint so the manager can see this came from a Nákup, plus
                // any free-text the worker entered on the receipt.
                Note            = string.IsNullOrWhiteSpace(p.Note) ? "Z nákupu" : $"Z nákupu — {p.Note}",
                // Map receipt photo to PhotoUrl so the existing Excel hyperlink + UI
                // thumbnail conventions just work without further changes.
                PhotoUrl        = p.ReceiptPhotoUrl,
                FromPurchase    = true,
                PurchaseId      = p.Id,
                IsService       = l.IsService
            }))
            .ToList();

        return usageRows.Concat(purchaseRows)
            .OrderByDescending(x => x.Date)
            .ThenByDescending(x => x.Id)
            .ToList();
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
            PhotoUrl        = saved.PhotoUrl,
            IsService       = saved.IsService
        });
    }

    // PUT /api/locations/{id}/materials/{usageId}
    [HttpPut("{id}/materials/{usageId}")]
    public async Task<ActionResult> UpdateMaterialUsage(int id, int usageId, UpdateMaterialUsageDto dto)
    {
        // Synthetic rows from MaterialPurchase lines use negated ids on the wire;
        // they are not editable through this endpoint — admin edits them via the
        // Nákupy admin tab. Be explicit so the slide-over panel can surface a
        // helpful message instead of a generic 404.
        if (usageId < 0)
            return BadRequest("Tento záznam vznikol z nákupu materiálu. Uprav ho cez Materiál → Nákupy.");

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
        if (usageId < 0)
            return BadRequest("Tento záznam vznikol z nákupu materiálu. Vymaž ho cez Materiál → Nákupy.");

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
        if (usageId < 0)
            return BadRequest("Účtenku k nákupu nahraj cez Materiál → Nákupy.");

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
        if (usageId < 0)
            return BadRequest("Účtenka patrí k nákupu — uprav ho cez Materiál → Nákupy.");

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

        // Same union as the slide-over panel (real usages + synthesised purchase lines)
        // so the Excel artefact the customer hands to their accountant matches what they
        // see in the admin UI exactly. Receipt URLs are mapped to PhotoUrl by the helper,
        // so the existing "Foto" hyperlink column in the export naturally lights up for
        // purchase-derived rows too — exactly the "exported with the excel like Záznamy"
        // behaviour requested on 2026-05-06.
        var entries = await BuildUnifiedMaterialEntriesAsync(id, f, t);

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

    // GET /api/locations/materials/export-all?from=YYYY-MM-DD&to=YYYY-MM-DD
    // Cross-Pracoviská Excel report: every active location's material consumption
    // at the SNAPSHOTTED unit price (UnitPriceAtTime / line.UnitPrice), so later
    // catalogue price edits don't rewrite the report. Synthesised purchase
    // pseudo-rows are included for Pracoviská that haven't been promoted via
    // Option A — same unified view the per-location panel shows.
    [HttpGet("materials/export-all")]
    public async Task<ActionResult> ExportAllLocationsMaterialsExcel(
        [FromQuery] string? from, [FromQuery] string? to)
    {
        var (f, t) = ParseDateRange(from, to);

        var locations = await _db.Locations
            .Where(l => l.IsActive)
            .OrderBy(l => l.Name)
            .ToListAsync();

        var perLocation = new List<(string LocationName, IEnumerable<MaterialUsageDto> Entries)>();
        foreach (var loc in locations)
        {
            var entries = await BuildUnifiedMaterialEntriesAsync(loc.Id, f, t);
            // Skip locations with nothing in this range — keeps the report tidy.
            if (entries.Count == 0) continue;
            perLocation.Add((loc.Name, entries));
        }

        var bytes = _excelExport.BuildAllLocationsMaterialReport(
            f,
            t.HasValue ? t.Value.AddDays(-1) : (DateTime?)null,
            perLocation);

        var rangeTag = f.HasValue && t.HasValue
            ? $"{f.Value:yyyy-MM-dd}_{t.Value.AddDays(-1):yyyy-MM-dd}"
            : DateTime.UtcNow.ToString("yyyy-MM-dd");
        var fileName = $"Spotreba_vsetky_pracoviska_{rangeTag}.xlsx";

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Náklady a zisk (per-location P&L) — PAYROLL_AND_PNL_PLAN.md
    //  Flagged at action level (not class level) because this controller owns
    //  existing actions that must stay reachable when the flag is off.
    // ─────────────────────────────────────────────────────────────────────────

    // GET /api/locations/{id}/pnl?from=YYYY-MM-DD&to=YYYY-MM-DD
    [HttpGet("{id}/pnl")]
    [RequireFeatureOrSuperAdmin("PayrollAndPnL")]
    public async Task<ActionResult<LocationPnlDto>> GetPnl(int id, [FromQuery] string? from, [FromQuery] string? to)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        var (f, t) = ParseDateRange(from, to);
        return await BuildPnlDtoAsync(loc, f, t);
    }

    // GET /api/locations/pnl-summary?from=YYYY-MM-DD&to=YYYY-MM-DD
    // Cross-location spending report for the Financie overview: one P&L row
    // per active Pracovisko (identical math to the per-location card), so
    // the manager sees wages + material per site for any time frame in one
    // call. The literal segment wins over the "{id}" route — no conflict.
    [HttpGet("pnl-summary")]
    [RequireFeatureOrSuperAdmin("PayrollAndPnL")]
    public async Task<ActionResult<List<LocationPnlDto>>> GetPnlSummary([FromQuery] string? from, [FromQuery] string? to)
    {
        var (f, t) = ParseDateRange(from, to);
        var locs = await _db.Locations
            .Where(l => l.IsActive)
            .OrderBy(l => l.Name)
            .ToListAsync();
        var rows = new List<LocationPnlDto>(locs.Count);
        foreach (var loc in locs)
            rows.Add(await BuildPnlDtoAsync(loc, f, t));

        // Invoice money assigned per location in the range (s DPH, any
        // document status except discarded) — the manager sees what scanned
        // invoices put on each site even before committing. Effective
        // location = line override ?? delivery list; dated by PurchaseDate,
        // consistent with the material view (the manual issue-date fix
        // cascades there too).
        var invLines = await (
            from l in _db.MaterialPurchaseLines
            join p in _db.MaterialPurchases on l.PurchaseId equals p.Id
            join d in _db.InvoiceDocuments on p.InvoiceDocumentId equals (int?)d.Id
            where d.Status != "discarded"
                  && (f == null || p.PurchaseDate >= f.Value)
                  && (t == null || p.PurchaseDate < t.Value)
            select new { LocId = l.LocationId ?? p.LocationId, l.LineTotal, l.VatRate, DocId = d.Id, DocTotal = d.TotalInclVat })
            .ToListAsync();

        // The customer-facing number is the INVOICE total ("vytlačené
        // spolu"), never our per-line arithmetic — per-line VAT rounding
        // drifts a few cents (live: column 74,81 vs card 74,75). Allocate
        // each document's printed total across its locations by line share
        // and put the rounding residual on the largest share, so the
        // column sums exactly to the Faktúry card.
        var perLocIncl = new Dictionary<int, decimal>();   // key: Location.Id, 0 = unassigned/Sklad
        foreach (var g in invLines.GroupBy(x => x.DocId))
        {
            var buckets = g
                .GroupBy(x => x.LocId)
                .Select(b => (LocId: b.Key ?? 0,
                              Incl: b.Sum(x => Math.Round(x.LineTotal + x.LineTotal * x.VatRate / 100m, 2, MidpointRounding.AwayFromZero))))
                .ToList();
            if (g.First().DocTotal is { } printed)
            {
                var residual = Math.Round(printed - buckets.Sum(b => b.Incl), 2, MidpointRounding.AwayFromZero);
                if (residual != 0m)
                {
                    var idx = 0;
                    for (var i = 1; i < buckets.Count; i++)
                        if (buckets[i].Incl > buckets[idx].Incl) idx = i;
                    buckets[idx] = (buckets[idx].LocId, buckets[idx].Incl + residual);
                }
            }
            foreach (var b in buckets)
                perLocIncl[b.LocId] = perLocIncl.GetValueOrDefault(b.LocId) + b.Incl;
        }
        foreach (var row in rows)
            row.InvoicedInclVat = perLocIncl.GetValueOrDefault(row.Location.Id);

        // Purchases with NO pracovisko at all (neither the delivery list nor
        // a row override) sit on Sklad and are invisible in the per-site rows
        // — the report could then never add up to the month's Materiál card
        // (live: PRESPOR 145 assigned 48,06 + HORNBACH 815 on Sklad 12,73 →
        // card 60,79 vs rows 48,06). Surface them as one synthetic row so
        // the manager sees what's still left to assign. Id 0 = not a real
        // location (the client renders it non-clickable). Manual purchases
        // are included via the left join — the Materiál card counts them too.
        var unassigned = await (
            from l in _db.MaterialPurchaseLines
            join p in _db.MaterialPurchases on l.PurchaseId equals p.Id
            join d0 in _db.InvoiceDocuments on p.InvoiceDocumentId equals (int?)d0.Id into dj
            from d in dj.DefaultIfEmpty()
            where (l.LocationId ?? p.LocationId) == null
                  && (d == null || d.Status != "discarded")
                  && (f == null || p.PurchaseDate >= f.Value)
                  && (t == null || p.PurchaseDate < t.Value)
            select new { l.LineTotal })
            .ToListAsync();
        if (unassigned.Count > 0)
        {
            rows.Add(new LocationPnlDto
            {
                Location = new PnlLocationDto { Id = 0, Name = "Sklad / Nepriradené" },
                Labour   = new PnlLabourDto(),
                Material = rows.Any(r => r.Material != null)
                    ? new PnlMaterialDto { Cost = unassigned.Sum(x => x.LineTotal) }
                    : null,
                // Same printed-total allocation as the real rows (bucket 0).
                InvoicedInclVat = perLocIncl.GetValueOrDefault(0),
            });
        }

        return rows;
    }

    // GET /api/locations/{id}/pnl/export?from=YYYY-MM-DD&to=YYYY-MM-DD
    // Same computation path as GetPnl, streamed as a Slovak XLSX workbook
    // mirroring the Náklady a zisk card.
    [HttpGet("{id}/pnl/export")]
    [RequireFeatureOrSuperAdmin("PayrollAndPnL")]
    public async Task<ActionResult> ExportPnlExcel(int id, [FromQuery] string? from, [FromQuery] string? to)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        var (f, t) = ParseDateRange(from, to);
        var pnl = await BuildPnlDtoAsync(loc, f, t);

        var bytes = _payrollExcelExport.BuildLocationPnlReport(
            pnl, f, t.HasValue ? t.Value.AddDays(-1) : (DateTime?)null);

        // Sanitise filename — strip diacritics and spaces (same as the material export)
        var safeName = string.Concat(
            loc.Name
                .Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                            != System.Globalization.UnicodeCategory.NonSpacingMark)
        ).Replace(' ', '_');

        var rangeTag = f.HasValue && t.HasValue
            ? $"{f.Value:yyyy-MM-dd}_{t.Value.AddDays(-1):yyyy-MM-dd}"
            : DateTime.UtcNow.ToString("yyyy-MM-dd");
        var fileName = $"Naklady_a_zisk_{safeName}_{rangeTag}.xlsx";

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // Shared P&L computation for GetPnl and ExportPnlExcel — keep the math in
    // exactly one place.
    private async Task<LocationPnlDto> BuildPnlDtoAsync(Location loc, DateTime? f, DateTime? t)
    {
        var id = loc.Id;

        // ── Labour: closed TimeEntries priced at the WageAtTime snapshot.
        // Never the live Employee.HourlyWage — that would rewrite history
        // (same inflation-protection rule as the Mzdy view).
        var teQuery = _db.TimeEntries
            .Where(e => e.LocationId == id && e.ClockOut != null);
        if (f.HasValue) teQuery = teQuery.Where(e => e.ClockIn >= f.Value);
        if (t.HasValue) teQuery = teQuery.Where(e => e.ClockIn <  t.Value);

        var rawEntries = await teQuery
            .Select(e => new
            {
                e.EmployeeId,
                EmployeeName = e.Employee.FirstName + " " + e.Employee.LastName,
                e.ClockIn,
                e.ClockOut,
                e.WageAtTime
            })
            .ToListAsync();

        var labourRows = rawEntries
            .GroupBy(e => new { e.EmployeeId, e.EmployeeName })
            .Select(g =>
            {
                var hours = g.Sum(x => (decimal)(x.ClockOut!.Value - x.ClockIn).TotalHours);
                var cost  = g.Sum(x => (decimal)(x.ClockOut!.Value - x.ClockIn).TotalHours * x.WageAtTime);
                return new PnlLabourRowDto
                {
                    EmployeeId   = g.Key.EmployeeId,
                    EmployeeName = g.Key.EmployeeName,
                    Hours        = Math.Round(hours, 2, MidpointRounding.AwayFromZero),
                    AvgWage      = hours == 0m ? null : Math.Round(cost / hours, 4, MidpointRounding.AwayFromZero),
                    Cost         = Math.Round(cost, 2, MidpointRounding.AwayFromZero)
                };
            })
            .OrderByDescending(r => r.Cost)
            .ToList();

        var labour = new PnlLabourDto
        {
            HoursWorked         = labourRows.Sum(r => r.Hours),
            Cost                = labourRows.Sum(r => r.Cost),
            BreakdownByEmployee = labourRows
        };

        // ── Material: same unified view as the Spotreba materiálu panel
        // (real MaterialUsage rows at UnitPriceAtTime + purchase-line
        // syntheses). Null when the MaterialPurchases flag is off for the
        // caller — the card then hides the row per plan §(g).
        PnlMaterialDto? material = null;
        var materialsOn = User.HasClaim("isSuperAdmin", "true")
            || await _db.FeatureFlags.AnyAsync(ff => ff.Key == "MaterialPurchases" && ff.Enabled);
        if (materialsOn)
        {
            var matEntries = await BuildUnifiedMaterialEntriesAsync(id, f, t);
            var matRows = matEntries
                .GroupBy(e => new { e.MaterialId, e.MaterialName, e.Unit })
                .Select(g =>
                {
                    var qty  = g.Sum(x => x.Quantity);
                    var cost = g.Sum(x => x.LineCost);
                    return new PnlMaterialRowDto
                    {
                        MaterialId   = g.Key.MaterialId,
                        MaterialName = g.Key.MaterialName,
                        Unit         = g.Key.Unit,
                        Quantity     = qty,
                        AvgUnitPrice = qty == 0m ? null : Math.Round(cost / qty, 4, MidpointRounding.AwayFromZero),
                        Cost         = Math.Round(cost, 2, MidpointRounding.AwayFromZero)
                    };
                })
                .OrderByDescending(r => r.Cost)
                .ToList();

            material = new PnlMaterialDto
            {
                Cost                = matRows.Sum(r => r.Cost),
                BreakdownByMaterial = matRows
            };
        }

        // ── Revenue / profit. Empty contract value → revenue "—", profit
        // hidden (null), cost side still shown. When the material section is
        // hidden by the flag, profit carries on without it per plan UX.
        var revenue = loc.ContractValue;
        decimal? profit = revenue.HasValue
            ? revenue.Value - labour.Cost - (material?.Cost ?? 0m)
            : null;

        return new LocationPnlDto
        {
            Location = new PnlLocationDto { Id = loc.Id, Name = loc.Name, ContractValue = loc.ContractValue },
            Labour   = labour,
            Material = material,
            Revenue  = revenue,
            Profit   = profit
        };
    }

    // PUT /api/locations/{id}/contract-value   body { contractValue: decimal? }
    [HttpPut("{id}/contract-value")]
    [RequireFeatureOrSuperAdmin("PayrollAndPnL")]
    public async Task<ActionResult> UpdateContractValue(int id, UpdateContractValueDto dto)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        if (dto.ContractValue is decimal v && v < 0)
            return BadRequest("Zmluvná hodnota nemôže byť záporná.");

        loc.ContractValue = dto.ContractValue;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
