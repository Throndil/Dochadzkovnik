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

    /// <summary>Active machines for the kiosk transport step (Auto / Stroj /
    /// Pešo, Fáza F3) — bagrista picks his bager instead of a car.</summary>
    [HttpGet("machines")]
    public async Task<ActionResult<List<MachineDto>>> GetMachines()
    {
        return await _db.Machines
            .Where(m => m.IsActive)
            .OrderBy(m => m.Name)
            .Select(m => new MachineDto { Id = m.Id, Name = m.Name, Note = m.Note, IsActive = m.IsActive })
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
            ClockIn = Now,
            // Snapshot the employee's current hourly wage at insert time so
            // payroll calculations don't get rewritten by future wage changes
            // (PAYROLL_AND_PNL_PLAN.md §design decision (a)). Null wage → 0
            // and the admin Mzdy view surfaces an amber "Sadzba nenastavená"
            // warning so the manager knows to fix it.
            WageAtTime = employee.HourlyWage ?? 0m
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
            Note = dto.Note,
            // Snapshot wage at insert (PAYROLL_AND_PNL_PLAN.md §(a)).
            WageAtTime = employee.HourlyWage ?? 0m
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
        // Guard: a long shift logged early in the day (e.g. 8h at 04:00) would
        // roll ClockIn back past midnight, attributing the entry — and its
        // hours — to the previous calendar day. Both the weekly grid and the
        // today-at-location roll-up bucket by ClockIn.Date, so it would never
        // turn the tile green or appear in the roll-up. Anchor it to the start
        // of `today` and keep the full hours instead.
        if (clockIn < today)
        {
            clockIn  = today;
            clockOut = today.AddHours(dto.HoursWorked);
        }

        // Validate car if provided
        if (dto.CarId.HasValue)
        {
            var car = await _db.Cars.FindAsync(dto.CarId.Value);
            if (car == null || !car.IsActive) return BadRequest("Neplatné vozidlo");
        }
        // Validate machine if provided (Auto / Stroj / Pešo — Fáza F3)
        if (dto.MachineId.HasValue)
        {
            var machine = await _db.Machines.FindAsync(dto.MachineId.Value);
            if (machine == null || !machine.IsActive) return BadRequest("Neplatný stroj");
        }

        var entry = new Models.TimeEntry
        {
            EmployeeId         = employee.Id,
            LocationId         = dto.LocationId,
            CarId              = dto.CarId,
            MachineId          = dto.MachineId,
            ClockIn            = clockIn,
            ClockOut           = clockOut,
            Note               = dto.Note,
            // Defaults to false when the kiosk omits the field (flag-off path).
            // See PROOF_OF_WORK_UX_PLAN.md §(d).
            ProofOfWorkSkipped = dto.ProofOfWorkSkipped ?? false,
            // Snapshot wage at insert (PAYROLL_AND_PNL_PLAN.md §(a)).
            WageAtTime         = employee.HourlyWage ?? 0m
        };

        _db.TimeEntries.Add(entry);
        await _db.SaveChangesAsync();

        var carPart = dto.CarId.HasValue
            ? $" ({(await _db.Cars.FindAsync(dto.CarId.Value))?.Name})"
            : dto.MachineId.HasValue
                ? $" ({(await _db.Machines.FindAsync(dto.MachineId.Value))?.Name})"
                : "";
        return Ok(new KioskResponseDto
        {
            Message      = $"Zaznamenaných {dto.HoursWorked:F1} hod. na {location.Name}{carPart}",
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            Timestamp    = clockOut,
            TimeEntryId  = entry.Id
        });
    }

    /// <summary>
    /// Auto-skip check for the kiosk proof-of-work step. Returns the most recent
    /// proof (photo or diary) the worker has already attached today at this
    /// Location within the past hour, if any. The kiosk uses this to skip the
    /// proof-pick step entirely and show a Slovak hint instead.
    ///
    /// See PROOF_OF_WORK_UX_PLAN.md §"Auto-skip". The "past hour" window is the
    /// V1 default; tune via the constant below if the customer wants it wider.
    /// </summary>
    [HttpPost("proof-exists")]
    public async Task<ActionResult<ProofExistsDto>> ProofExists([FromBody] ProofExistsRequestDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        var date = (dto.Date?.Date) ?? Now.Date;
        var cutoffUtc = DateTime.UtcNow.AddHours(-1);

        // WorkPhoto path: standalone "Nahrať fotografiu" tile.
        var photo = await _db.WorkPhotos
            .Where(p => p.EmployeeId == employee.Id
                     && p.LocationId == dto.LocationId
                     && p.CreatedAt >= cutoffUtc)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.CreatedAt })
            .FirstOrDefaultAsync();

        // WorkDiary path: linked or standalone diary submitted today.
        var diary = await _db.WorkDiaries
            .Where(d => d.EmployeeId == employee.Id
                     && d.LocationId == dto.LocationId
                     && d.Date == date
                     && d.CreatedAt >= cutoffUtc)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.CreatedAt })
            .FirstOrDefaultAsync();

        // TimeEntry path: in-modal photo attached during a previous šichta.
        var entryPhoto = await _db.TimeEntries
            .Where(t => t.EmployeeId == employee.Id
                     && t.LocationId == dto.LocationId
                     && t.PhotoUrl != null && t.PhotoUrl != ""
                     && t.CreatedAt >= cutoffUtc)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.CreatedAt })
            .FirstOrDefaultAsync();

        // Pick the latest. Avoid null arithmetic.
        DateTime? bestAt = null;
        string? bestSource = null;
        if (photo != null) { bestAt = photo.CreatedAt; bestSource = "photo"; }
        if (diary != null && (bestAt == null || diary.CreatedAt > bestAt)) { bestAt = diary.CreatedAt; bestSource = "diary"; }
        if (entryPhoto != null && (bestAt == null || entryPhoto.CreatedAt > bestAt)) { bestAt = entryPhoto.CreatedAt; bestSource = "photo"; }

        // Convert UTC back to Europe/Bratislava local for the Slovak hint copy.
        var localAt = bestAt.HasValue ? TimeZoneInfo.ConvertTimeFromUtc(bestAt.Value, _tz) : (DateTime?)null;

        return Ok(new ProofExistsDto
        {
            Exists = bestAt.HasValue,
            Source = bestSource,
            At     = localAt
        });
    }

    /// <summary>
    /// Roll-up of today's TimeEntries at a given Location. Used by the kiosk
    /// hours step so the next worker arriving on site can read what colleagues
    /// already did and avoid duplicate notes. PIN-validated. Read-only.
    /// </summary>
    [HttpPost("today-at-location")]
    public async Task<ActionResult<List<TodayAtLocationEntryDto>>> TodayAtLocation([FromBody] TodayAtLocationRequestDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        var today = Now.Date;
        var tomorrow = today.AddDays(1);

        var rows = await _db.TimeEntries
            .Where(t => t.LocationId == dto.LocationId
                     && t.ClockIn >= today
                     && t.ClockIn <  tomorrow)
            .OrderBy(t => t.ClockIn)
            .Select(t => new TodayAtLocationEntryDto
            {
                EmployeeId   = t.EmployeeId,
                EmployeeName = t.Employee.FirstName + " " + t.Employee.LastName,
                ClockIn      = t.ClockIn,
                HoursWorked  = t.ClockOut.HasValue
                    ? (t.ClockOut.Value - t.ClockIn).TotalHours
                    : (double?)null,
                Note         = t.Note,
                // Pull the linked diary body (if any). When a worker submitted
                // via the diary tile, the TimeEntry note carries only the
                // "Stavebný denník" marker — the actual content lives here.
                DiaryBody    = _db.WorkDiaries
                                  .Where(d => d.TimeEntryId == t.Id)
                                  .OrderBy(d => d.Id)
                                  .Select(d => d.BodyText)
                                  .FirstOrDefault(),
                IsMine       = t.EmployeeId == employee.Id
            })
            .ToListAsync();

        return Ok(rows);
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

        // Spolu column shows full-month totals. When the 7-day window crosses a month
        // boundary (e.g. week of 27 Apr – 3 May) we return BOTH months' totals separately
        // so the manager can see the April figure and the May figure in one view.
        var month1Start = new DateTime(start.Year, start.Month, 1);
        var month1End   = month1Start.AddMonths(1);   // exclusive upper bound for month 1

        var spansTwoMonths = end > month1End;

        DateTime month2Start = month1End;                       // = first day of month 2
        DateTime month2End   = month1End.AddMonths(1);          // = first day of month 3

        // Fetch range covers full month 1 + (if needed) full month 2 so both totals are correct.
        var fetchStart = month1Start;
        var fetchEnd   = spansTwoMonths ? month2End : month1End;

        var employees = await _db.Employees
            .Where(e => e.IsActive)
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .ToListAsync();

        var entries = await _db.TimeEntries
            .Include(t => t.Location)
            .Where(t => t.ClockIn >= fetchStart && t.ClockIn < fetchEnd)
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
                // Full-month total for month 1 (April when viewing the Apr–May boundary week)
                TotalHours = empEntries
                    .Where(t => t.ClockOut.HasValue && t.ClockIn >= month1Start && t.ClockIn < month1End)
                    .Sum(t => (t.ClockOut!.Value - t.ClockIn).TotalHours),
                // Full-month total for month 2 (May); zero when the week is entirely within one month
                TotalHoursMonth2 = spansTwoMonths
                    ? empEntries
                        .Where(t => t.ClockOut.HasValue && t.ClockIn >= month2Start && t.ClockIn < month2End)
                        .Sum(t => (t.ClockOut!.Value - t.ClockIn).TotalHours)
                    : 0
            };
        }).ToList();

        // Slovak full month names, capitalised — shown in the kiosk Spolu header
        // and per-month sub-totals when the viewed week straddles a month boundary.
        string[] slovakMonths = ["Január", "Február", "Marec", "Apríl", "Máj", "Jún", "Júl", "August", "September", "Október", "November", "December"];

        return Ok(new WeeklyOverviewDto
        {
            WeekStart      = start,
            Days           = days,
            Rows           = rows,
            SpansTwoMonths = spansTwoMonths,
            Month1Label    = slovakMonths[month1Start.Month - 1],
            Month2Label    = spansTwoMonths ? slovakMonths[month2Start.Month - 1] : null
        });
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

        var locationName = (await _db.Locations.FindAsync(entry.LocationId))?.Name;
        var folder = CloudinaryFolders.WorkPhotos(entry.LocationId, locationName, entry.ClockIn);

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

        var folder = CloudinaryFolders.WorkPhotos(locationId, location.Name, DateTime.UtcNow);

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

    // =================================================================
    //  "Treba pripomenúť" (Option A + D)
    //  Public, no-auth endpoints used by:
    //    • the kiosk public banner (everyone can see who is missing days)
    //    • the personal red banner shown after PIN entry
    //    • the admin Notifikácie page
    //  Designed to be cheap (one read of Employees + one ranged read of TimeEntries).
    // =================================================================

    /// <summary>
    /// Returns the list of active employees who have no TimeEntry for one or more of the
    /// past 2 calendar days (excluding today). If WorkingDaysOnly is enabled in
    /// NotificationConfig, weekend dates are skipped.
    /// </summary>
    [HttpGet("missing-hours-overview")]
    public async Task<ActionResult<MissingHoursOverviewDto>> GetMissingHoursOverview()
    {
        var datesToCheck = await ComputeDatesToCheck();
        var result = new MissingHoursOverviewDto
        {
            CheckedDates = datesToCheck.Select(d => d.ToString("yyyy-MM-dd")).ToList(),
            Employees = new List<EmployeeMissingDaysDto>()
        };

        if (datesToCheck.Count == 0) return Ok(result);

        var (rangeStartUtc, rangeEndUtc) = ToUtcRange(datesToCheck);

        var employees = await _db.Employees
            .Where(e => e.IsActive)
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .ToListAsync();

        var entries = await _db.TimeEntries
            .Where(t => t.ClockIn >= rangeStartUtc && t.ClockIn < rangeEndUtc)
            .Select(t => new { t.EmployeeId, t.ClockIn })
            .ToListAsync();

        // employee → set of local dates with at least one entry
        var activityByEmp = entries
            .GroupBy(e => e.EmployeeId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(x.ClockIn, _tz))).ToHashSet());

        foreach (var emp in employees)
        {
            var activity = activityByEmp.GetValueOrDefault(emp.Id, new HashSet<DateOnly>());
            var missing = datesToCheck.Where(d => !activity.Contains(d)).ToList();
            if (missing.Count == 0) continue;

            result.Employees.Add(new EmployeeMissingDaysDto
            {
                Id           = emp.Id,
                FirstName    = emp.FirstName,
                LastName     = emp.LastName,
                FullName     = $"{emp.FirstName} {emp.LastName}",
                PhotoUrl     = emp.PhotoUrl,
                // PhoneNumber deliberately omitted — this endpoint is anonymous and
                // any phone number returned is scrapeable PII visible on the public
                // kiosk display. Managers look phone numbers up via the JWT-protected
                // /api/employees admin API instead.
                MissingDates = missing.Select(d => d.ToString("yyyy-MM-dd")).ToList()
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Returns the missing-day list for the worker who owns the supplied PIN.
    /// Used by the kiosk to render the personal red banner after PIN entry.
    /// </summary>
    [HttpPost("my-missing-days")]
    public async Task<ActionResult<MyMissingDaysDto>> GetMyMissingDays([FromBody] ClockOutDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        var datesToCheck = await ComputeDatesToCheck();
        var resp = new MyMissingDaysDto
        {
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            MissingDates = new List<string>()
        };
        if (datesToCheck.Count == 0) return Ok(resp);

        var (rangeStartUtc, rangeEndUtc) = ToUtcRange(datesToCheck);
        var entries = await _db.TimeEntries
            .Where(t => t.EmployeeId == employee.Id && t.ClockIn >= rangeStartUtc && t.ClockIn < rangeEndUtc)
            .Select(t => t.ClockIn)
            .ToListAsync();

        var activeDates = entries
            .Select(c => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(c, _tz)))
            .ToHashSet();

        resp.MissingDates = datesToCheck
            .Where(d => !activeDates.Contains(d))
            .Select(d => d.ToString("yyyy-MM-dd"))
            .ToList();

        return Ok(resp);
    }

    private async Task<List<DateOnly>> ComputeDatesToCheck()
    {
        var today = DateOnly.FromDateTime(Now);
        var config = await _db.NotificationConfigs.FirstOrDefaultAsync(c => c.Id == 1);
        var workingDaysOnly = config?.WorkingDaysOnly ?? true;

        var dates = new List<DateOnly>();
        for (int i = 1; i <= 7; i++)
        {
            var d = today.AddDays(-i);
            if (workingDaysOnly && (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday))
                continue;
            dates.Add(d);
        }
        // Oldest first for nicer rendering.
        dates.Reverse();
        return dates;
    }

    private (DateTime startUtc, DateTime endUtc) ToUtcRange(List<DateOnly> dates)
    {
        var earliest = dates.Min();
        var latestPlusOne = dates.Max().AddDays(1);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(earliest.ToDateTime(TimeOnly.MinValue), _tz);
        var endUtc   = TimeZoneInfo.ConvertTimeToUtc(latestPlusOne.ToDateTime(TimeOnly.MinValue), _tz);
        return (startUtc, endUtc);
    }

    // POST /api/kiosk/decline-notifications
    // PIN-authenticated. Records the reason a worker gave for declining push notifications.
    // Sets NotificationsEnabled = false so the background service stops reminding them.
    [HttpPost("decline-notifications")]
    public async Task<ActionResult> DeclineNotifications([FromBody] DeclineNotificationsDto dto)
    {
        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        employee.NotificationsEnabled = false;
        employee.NotificationsDeclineReason = dto.Reason?.Trim() ?? string.Empty;
        employee.UpdatedAt = Now;
        await _db.SaveChangesAsync();

        return Ok();
    }

    private async Task<Models.Employee?> FindEmployeeByPin(string pin)
    {
        var employees = await _db.Employees
            .Where(e => e.IsActive)
            .ToListAsync();

        return employees.FirstOrDefault(e => _pinHasher.Verify(e.Pin, pin));
    }
}
