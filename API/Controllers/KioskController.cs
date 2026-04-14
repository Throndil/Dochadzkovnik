using API.Data;
using API.DTOs;
using API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class KioskController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPinHasher _pinHasher;
    private readonly IBlobStorageService? _blob;
    private static readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    private static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);

    public KioskController(AppDbContext db, IPinHasher pinHasher, IBlobStorageService? blob = null)
    {
        _db = db;
        _pinHasher = pinHasher;
        _blob = blob;
    }

    [HttpGet("locations")]
    public async Task<ActionResult<List<LocationDto>>> GetLocations()
    {
        return await _db.Locations
            .Where(l => l.IsActive)
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

    [HttpGet("cars")]
    public async Task<ActionResult<List<CarDto>>> GetCars()
    {
        return await _db.Cars
            .Where(c => c.IsActive)
            .Select(c => new CarDto { Id = c.Id, Name = c.Name, LicensePlate = c.LicensePlate, IsActive = c.IsActive })
            .ToListAsync();
    }

    [HttpPost("clock-in")]
    public async Task<ActionResult<KioskResponseDto>> ClockIn(ClockInDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null)
            return Unauthorized("Neplatný PIN");

        var openEntry = await _db.TimeEntries
            .FirstOrDefaultAsync(t => t.EmployeeId == employee.Id && t.ClockOut == null);

        if (openEntry != null)
            return BadRequest("Už ste prihlásený. Najskôr sa odhláste.");

        var location = await _db.Locations.FindAsync(dto.LocationId);
        if (location == null || !location.IsActive)
            return BadRequest("Neplatné pracovisko");

        var entry = new Models.TimeEntry
        {
            EmployeeId = employee.Id,
            LocationId = dto.LocationId,
            ClockIn = Now
        };

        _db.TimeEntries.Add(entry);
        await _db.SaveChangesAsync();

        return Ok(new KioskResponseDto
        {
            Message = $"Príchod na {location.Name}",
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            Timestamp = entry.ClockIn
        });
    }

    [HttpPost("clock-out")]
    public async Task<ActionResult<KioskResponseDto>> ClockOut(ClockOutDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null)
            return Unauthorized("Neplatný PIN");

        var openEntry = await _db.TimeEntries
            .Include(t => t.Location)
            .FirstOrDefaultAsync(t => t.EmployeeId == employee.Id && t.ClockOut == null);

        if (openEntry == null)
            return BadRequest("Momentálne nie ste prihlásený");

        openEntry.ClockOut = Now;
        openEntry.Note = dto.Note;
        await _db.SaveChangesAsync();

        var hours = (openEntry.ClockOut.Value - openEntry.ClockIn).TotalHours;

        return Ok(new KioskResponseDto
        {
            Message = $"Odchod z {openEntry.Location.Name}. Odpracované: {hours:F1} hod.",
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            Timestamp = openEntry.ClockOut.Value
        });
    }

    [HttpPost("my-hours")]
    public async Task<ActionResult<List<TimeEntryDto>>> GetMyHours(MyHoursDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null)
            return Unauthorized("Neplatný PIN");

        var query = _db.TimeEntries
            .Include(t => t.Location)
            .Where(t => t.EmployeeId == employee.Id);

        if (dto.From.HasValue)
            query = query.Where(t => t.ClockIn >= dto.From.Value.Date);

        if (dto.To.HasValue)
            query = query.Where(t => t.ClockIn < dto.To.Value.Date.AddDays(1));

        var entries = await query
            .OrderByDescending(t => t.ClockIn)
            .Select(t => new TimeEntryDto
            {
                Id = t.Id,
                EmployeeId = t.EmployeeId,
                EmployeeName = employee.FirstName + " " + employee.LastName,
                LocationId = t.LocationId,
                LocationName = t.Location.Name,
                ClockIn = t.ClockIn,
                ClockOut = t.ClockOut,
                HoursWorked = t.ClockOut.HasValue
                    ? (t.ClockOut.Value - t.ClockIn).TotalHours
                    : null,
                Note = t.Note,
                PhotoUrl = t.PhotoUrl
            })
            .ToListAsync();

        return Ok(entries);
    }

    [HttpPost("manual-entry")]
    public async Task<ActionResult<KioskResponseDto>> ManualEntry(ManualEntryDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null)
            return Unauthorized("Neplatný PIN");

        var location = await _db.Locations.FindAsync(dto.LocationId);
        if (location == null || !location.IsActive)
            return BadRequest("Neplatné pracovisko");

        if (dto.ClockOut <= dto.ClockIn)
            return BadRequest("Odchod musí byť po príchode");

        var entry = new Models.TimeEntry
        {
            EmployeeId = employee.Id,
            LocationId = dto.LocationId,
            ClockIn = dto.ClockIn,
            ClockOut = dto.ClockOut,
            Note = dto.Note
        };

        _db.TimeEntries.Add(entry);
        await _db.SaveChangesAsync();

        var hours = (dto.ClockOut - dto.ClockIn).TotalHours;
        return Ok(new KioskResponseDto
        {
            Message = $"Záznam uložený na {location.Name}, {hours:F1} hod.",
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            Timestamp = dto.ClockOut,
            TimeEntryId = entry.Id
        });
    }

    [HttpPost("status")]
    public async Task<ActionResult<KioskStatusDto>> GetStatus([FromBody] ClockOutDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null)
            return Unauthorized("Neplatný PIN");

        var openEntry = await _db.TimeEntries
            .Include(t => t.Location)
            .FirstOrDefaultAsync(t => t.EmployeeId == employee.Id && t.ClockOut == null);

        return Ok(new KioskStatusDto
        {
            EmployeeId   = employee.Id,
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            IsClockedIn  = openEntry != null,
            ClockInTime  = openEntry?.ClockIn,
            LocationName = openEntry?.Location.Name
        });
    }

    [HttpPost("log-hours")]
    public async Task<ActionResult<KioskResponseDto>> LogHours(LogHoursDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null)
            return Unauthorized("Neplatný PIN");

        var location = await _db.Locations.FindAsync(dto.LocationId);
        if (location == null || !location.IsActive)
            return BadRequest("Neplatné pracovisko");

        var today = dto.Date?.Date ?? Now.Date;
        // ClockOut = current time for today, or 17:00 for a past date
        var clockOut = today == Now.Date ? Now : today.AddHours(17);
        var clockIn  = clockOut.AddHours(-dto.HoursWorked);

        // Validate car if provided
        if (dto.CarId.HasValue)
        {
            var car = await _db.Cars.FindAsync(dto.CarId.Value);
            if (car == null || !car.IsActive) return BadRequest("Neplatné vozidlo");
        }

        var entry = new Models.TimeEntry
        {
            EmployeeId = employee.Id,
            LocationId = dto.LocationId,
            CarId      = dto.CarId,
            ClockIn    = clockIn,
            ClockOut   = clockOut,
            Note       = dto.Note
        };

        _db.TimeEntries.Add(entry);
        await _db.SaveChangesAsync();

        var carPart = dto.CarId.HasValue
            ? $" ({(await _db.Cars.FindAsync(dto.CarId.Value))?.Name})"
            : "";
        return Ok(new KioskResponseDto
        {
            Message      = $"Zaznamenaných {dto.HoursWorked:F1} hod. na {location.Name}{carPart}",
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            Timestamp    = clockOut,
            TimeEntryId  = entry.Id
        });
    }

    [HttpGet("overview")]
    public async Task<ActionResult<WeeklyOverviewDto>> GetOverview([FromQuery] DateTime? weekStart)
    {
        var now = Now;
        var dayOfWeek = (int)now.DayOfWeek;
        var mondayOffset = dayOfWeek == 0 ? -6 : 1 - dayOfWeek;
        var monday = now.Date.AddDays(mondayOffset);

        var start = weekStart?.Date ?? monday;
        var end = start.AddDays(7);

        var employees = await _db.Employees
            .Where(e => e.IsActive)
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .ToListAsync();

        var entries = await _db.TimeEntries
            .Include(t => t.Location)
            .Where(t => t.ClockIn >= start && t.ClockIn < end)
            .ToListAsync();

        // Filter to only active employees
        var activeIds = employees.Select(e => e.Id).ToHashSet();
        entries = entries.Where(t => activeIds.Contains(t.EmployeeId)).ToList();

        var days = Enumerable.Range(0, 7).Select(i => start.AddDays(i)).ToList();

        var rows = employees.Select(emp =>
        {
            var empEntries = entries.Where(t => t.EmployeeId == emp.Id).ToList();
            return new WeeklyRowDto
            {
                EmployeeId = emp.Id,
                EmployeeName = $"{emp.FirstName} {emp.LastName}",
                PhotoUrl = emp.PhotoUrl,
                Days = days.Select(day =>
                {
                    var dayEntries = empEntries
                        .Where(t => t.ClockIn.Date == day && t.ClockOut.HasValue)
                        .Select(t => new WeeklyEntryDto
                        {
                            LocationName = t.Location.Name,
                            Hours = (t.ClockOut!.Value - t.ClockIn).TotalHours,
                            Note = t.Note
                        }).ToList();
                    return new WeeklyDayDto { Date = day, Entries = dayEntries };
                }).ToList(),
                TotalHours = empEntries.Where(t => t.ClockOut.HasValue)
                    .Sum(t => (t.ClockOut!.Value - t.ClockIn).TotalHours)
            };
        }).ToList();

        return Ok(new WeeklyOverviewDto { WeekStart = start, Days = days, Rows = rows });
    }

    // POST /api/kiosk/photo/{timeEntryId}
    // PIN-authenticated, no JWT required — for workers uploading photos via kiosk.
    // Accepts up to 5 photos at once; stores comma-separated URLs in TimeEntry.PhotoUrl.
    [HttpPost("photo/{timeEntryId}")]
    public async Task<ActionResult<PhotoUploadResultDto>> UploadEntryPhoto(int timeEntryId, [FromForm] string pin, [FromForm] IFormFileCollection photos)
    {
        var employee = await FindEmployeeByPin(pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        var entry = await _db.TimeEntries.FindAsync(timeEntryId);
        if (entry == null) return NotFound();

        // Verify this entry actually belongs to the authenticating employee
        if (entry.EmployeeId != employee.Id) return Forbid();

        if (_blob == null) return StatusCode(503, "Storage service not configured");
        if (photos == null || photos.Count == 0) return BadRequest("No files provided");

        // Parse existing URLs (comma-separated; single legacy URLs are handled transparently)
        var existingUrls = string.IsNullOrEmpty(entry.PhotoUrl)
            ? new List<string>()
            : entry.PhotoUrl.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        const int maxPhotos = 5;
        int slots = maxPhotos - existingUrls.Count;
        if (slots <= 0) return BadRequest("Maximum 5 photos already uploaded for this entry");

        var month = entry.ClockIn.ToString("yyyy-MM");
        var folder = $"work-photos/{entry.LocationId}/{month}";

        var newUrls = new List<string>();
        foreach (var photo in photos.Take(slots))
        {
            if (photo.Length == 0) continue;
            if (photo.Length > 20 * 1024 * 1024) continue; // skip oversized, don't abort
            await using var stream = photo.OpenReadStream();
            var url = await _blob.UploadAsync(stream, photo.FileName, folder);
            newUrls.Add(url);
        }

        existingUrls.AddRange(newUrls);
        entry.PhotoUrl = string.Join(",", existingUrls);
        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new PhotoUploadResultDto { PhotoUrl = entry.PhotoUrl });
    }

    // POST /api/kiosk/work-photo
    // PIN-authenticated, no JWT — standalone proof-of-work photo (not tied to a time entry)
    [HttpPost("work-photo")]
    public async Task<ActionResult<WorkPhotoResultDto>> UploadWorkPhoto([FromForm] string pin, [FromForm] int locationId, IFormFile photo)
    {
        var employee = await FindEmployeeByPin(pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        var location = await _db.Locations.FindAsync(locationId);
        if (location == null || !location.IsActive)
            return BadRequest("Neplatné pracovisko");

        if (_blob == null) return StatusCode(503, "Storage service not configured");
        if (photo == null || photo.Length == 0) return BadRequest("No file provided");
        if (photo.Length > 20 * 1024 * 1024) return BadRequest("File too large (max 20 MB)");

        // Enforce daily photo limit per employee (max 5 work photos per day)
        var todayStart = DateTime.UtcNow.Date;
        var todayEnd   = todayStart.AddDays(1);
        var todayCount = await _db.WorkPhotos
            .CountAsync(w => w.EmployeeId == employee.Id
                          && w.CreatedAt >= todayStart
                          && w.CreatedAt < todayEnd);
        if (todayCount >= 5)
            return BadRequest("Denný limit fotografií (5) bol dosiahnutý");

        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var folder = $"work-photos/{locationId}/{month}";

        await using var stream = photo.OpenReadStream();
        var url = await _blob.UploadAsync(stream, photo.FileName, folder);

        var workPhoto = new Models.WorkPhoto
        {
            EmployeeId = employee.Id,
            LocationId = locationId,
            PhotoUrl   = url,
            CreatedAt  = DateTime.UtcNow
        };

        _db.WorkPhotos.Add(workPhoto);
        await _db.SaveChangesAsync();

        return Ok(new WorkPhotoResultDto
        {
            PhotoUrl       = url,
            EmployeeName   = $"{employee.FirstName} {employee.LastName}",
            LocationName   = location.Name,
            CreatedAt      = workPhoto.CreatedAt,
            RemainingToday = 5 - (todayCount + 1)   // todayCount was before this upload
        });
    }

    private async Task<Models.Employee?> FindEmployeeByPin(string pin)
    {
        var employees = await _db.Employees
            .Where(e => e.IsActive)
            .ToListAsync();

        return employees.FirstOrDefault(e => _pinHasher.Verify(e.Pin, pin));
    }
}
