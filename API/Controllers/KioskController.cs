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
    private static readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    private static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);

    public KioskController(AppDbContext db, IPinHasher pinHasher)
    {
        _db = db;
        _pinHasher = pinHasher;
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
                Note = t.Note
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
            Timestamp = dto.ClockOut
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
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            IsClockedIn = openEntry != null,
            ClockInTime = openEntry?.ClockIn,
            LocationName = openEntry?.Location.Name
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
