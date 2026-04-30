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
public class EmployeesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPinHasher _pinHasher;
    private readonly IBlobStorageService? _blobStorage;
    private readonly IConfiguration _config;

    public EmployeesController(AppDbContext db, IPinHasher pinHasher,
        IConfiguration config, IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _pinHasher = pinHasher;
        _blobStorage = blobStorage;
        _config = config;
    }

    private static readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    /// <summary>
    /// Admin variant of "Treba pripomenúť": same shape as the anonymous kiosk endpoint
    /// but **includes PhoneNumber** so a manager on the Notifikácie page can call/SMS
    /// the worker. JWT-protected. Phone numbers MUST NOT be served via the kiosk
    /// endpoint — see KioskController.GetMissingHoursOverview for the no-phone variant.
    /// </summary>
    [HttpGet("missing-hours-overview")]
    public async Task<ActionResult<MissingHoursOverviewAdminDto>> GetMissingHoursOverview()
    {
        // Inline the date computation rather than depending on a private KioskController helper.
        // Keeps the two endpoints fully decoupled — one anon and PII-free, one JWT and rich.
        var config = await _db.NotificationConfigs.FirstOrDefaultAsync(c => c.Id == 1);
        var workingDaysOnly = config?.WorkingDaysOnly ?? true;

        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz));
        var datesToCheck = new List<DateOnly>();
        for (int back = 1; back <= 7 && datesToCheck.Count < 2; back++)
        {
            var d = todayLocal.AddDays(-back);
            if (workingDaysOnly && (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday))
                continue;
            datesToCheck.Add(d);
        }
        datesToCheck.Reverse();

        var result = new MissingHoursOverviewAdminDto
        {
            CheckedDates = datesToCheck.Select(d => d.ToString("yyyy-MM-dd")).ToList(),
            Employees = new List<EmployeeMissingDaysAdminDto>()
        };
        if (datesToCheck.Count == 0) return Ok(result);

        var rangeStartUtc = TimeZoneInfo.ConvertTimeToUtc(datesToCheck.First().ToDateTime(TimeOnly.MinValue), _tz);
        var rangeEndUtc   = TimeZoneInfo.ConvertTimeToUtc(datesToCheck.Last().AddDays(1).ToDateTime(TimeOnly.MinValue), _tz);

        const string systemPin = "SYSTEM_ADMIN_GALLERY_UPLOADER";
        var employees = await _db.Employees
            .Where(e => e.IsActive && e.Pin != systemPin)
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .ToListAsync();

        var entries = await _db.TimeEntries
            .Where(t => t.ClockIn >= rangeStartUtc && t.ClockIn < rangeEndUtc)
            .Select(t => new { t.EmployeeId, t.ClockIn })
            .ToListAsync();

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

            result.Employees.Add(new EmployeeMissingDaysAdminDto
            {
                Id           = emp.Id,
                FirstName    = emp.FirstName,
                LastName     = emp.LastName,
                FullName     = $"{emp.FirstName} {emp.LastName}",
                PhotoUrl     = emp.PhotoUrl,
                PhoneNumber  = emp.PhoneNumber,
                MissingDates = missing.Select(d => d.ToString("yyyy-MM-dd")).ToList()
            });
        }

        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<List<EmployeeDto>>> GetAll()
    {
        const string systemPin = "SYSTEM_ADMIN_GALLERY_UPLOADER";
        return await _db.Employees
            .Where(e => e.Pin != systemPin)
            .Select(e => new EmployeeDto
            {
                Id = e.Id,
                FirstName = e.FirstName,
                LastName = e.LastName,
                PhoneNumber = e.PhoneNumber,
                Address = e.Address,
                City = e.City,
                PhotoUrl = e.PhotoUrl,
                IsActive = e.IsActive,
                CreatedAt = e.CreatedAt,
                NotificationsDeclineReason = e.NotificationsDeclineReason
            })
            .ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EmployeeDto>> Get(int id)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        return new EmployeeDto
        {
            Id = emp.Id,
            FirstName = emp.FirstName,
            LastName = emp.LastName,
            PhoneNumber = emp.PhoneNumber,
            Address = emp.Address,
            City = emp.City,
            PhotoUrl = emp.PhotoUrl,
            IsActive = emp.IsActive,
            CreatedAt = emp.CreatedAt,
            PinPlain = emp.PinPlain,
            NotificationsDeclineReason = emp.NotificationsDeclineReason
        };
    }

    [HttpGet("generate-pin")]
    public async Task<ActionResult<object>> GeneratePin()
    {
        var allHashes = await _db.Employees.Select(e => e.Pin).ToListAsync();
        string pin;
        var attempts = 0;
        do
        {
            pin = Random.Shared.Next(1000, 10000).ToString();
            attempts++;
            if (attempts > 500)
                return StatusCode(503, "Nedá sa vygenerovať unikátny PIN.");
        } while (allHashes.Any(h => _pinHasher.Verify(h, pin)));

        return Ok(new { pin });
    }

    [HttpPost]
    public async Task<ActionResult<EmployeeDto>> Create(CreateEmployeeDto dto)
    {
        if (await IsPinTaken(dto.Pin))
            return Conflict("Tento PIN je už priradený inému zamestnancovi.");

        var employee = new Employee
        {
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Pin = _pinHasher.Hash(dto.Pin),
            PinPlain = dto.Pin,
            PhoneNumber = dto.PhoneNumber,
            Address = dto.Address,
            City = dto.City
        };

        _db.Employees.Add(employee);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = employee.Id }, new EmployeeDto
        {
            Id = employee.Id,
            FirstName = employee.FirstName,
            LastName = employee.LastName,
            IsActive = employee.IsActive,
            CreatedAt = employee.CreatedAt
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateEmployeeDto dto)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        emp.FirstName = dto.FirstName;
        emp.LastName = dto.LastName;
        emp.PhoneNumber = dto.PhoneNumber;
        emp.Address = dto.Address;
        emp.City = dto.City;
        emp.IsActive = dto.IsActive;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        emp.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/toggle-active")]
    public async Task<ActionResult> ToggleActive(int id)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        emp.IsActive = !emp.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/pin")]
    public async Task<ActionResult> SetPin(int id, SetPinDto dto)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        if (await IsPinTaken(dto.Pin, excludeEmployeeId: id))
            return Conflict("Tento PIN je už priradený inému zamestnancovi.");

        emp.Pin = _pinHasher.Hash(dto.Pin);
        emp.PinPlain = dto.Pin;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}/permanent")]
    public async Task<ActionResult> DeletePermanent(int id)
    {
        var emp = await _db.Employees
            .Include(e => e.TimeEntries)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (emp == null) return NotFound();

        _db.TimeEntries.RemoveRange(emp.TimeEntries);
        _db.Employees.Remove(emp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/photo")]
    public async Task<ActionResult<string>> UploadPhoto(int id, IFormFile file)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        if (file.Length == 0 || file.Length > 5 * 1024 * 1024)
            return BadRequest("File must be between 1 byte and 5MB");

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return BadRequest("Only image files (jpg, png, gif, webp) are allowed");

        if (_blobStorage == null)
            return StatusCode(503, "Photo storage is not configured");

        if (!string.IsNullOrEmpty(emp.PhotoUrl))
        {
            await _blobStorage.DeleteAsync(emp.PhotoUrl, "employee-photos");
        }

        using var stream = file.OpenReadStream();
        emp.PhotoUrl = await _blobStorage.UploadAsync(stream, file.FileName, "employee-photos");
        await _db.SaveChangesAsync();

        return Ok(emp.PhotoUrl);
    }

    private async Task<bool> IsPinTaken(string pin, int? excludeEmployeeId = null)
    {
        const string systemPin = "SYSTEM_ADMIN_GALLERY_UPLOADER";
        var query = _db.Employees
            .Where(e => e.Pin != systemPin); // sentinel has a plain-text pin, not a hash — skip it
        if (excludeEmployeeId.HasValue)
            query = query.Where(e => e.Id != excludeEmployeeId.Value);

        var hashes = await query.Select(e => e.Pin).ToListAsync();
        return hashes.Any(h => _pinHasher.Verify(h, pin));
    }
}
