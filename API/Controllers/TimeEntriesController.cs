using API.Data;
using API.DTOs;
using API.Models;
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

    public TimeEntriesController(AppDbContext db)
    {
        _db = db;
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
                ClockIn = t.ClockIn,
                ClockOut = t.ClockOut,
                HoursWorked = t.ClockOut.HasValue
                    ? (t.ClockOut.Value - t.ClockIn).TotalHours
                    : null,
                Note = t.Note
            })
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<TimeEntryDto>> Create(CreateTimeEntryDto dto)
    {
        var employee = await _db.Employees.FindAsync(dto.EmployeeId);
        if (employee == null) return BadRequest("Employee not found");

        var location = await _db.Locations.FindAsync(dto.LocationId);
        if (location == null) return BadRequest("Location not found");

        var entry = new TimeEntry
        {
            EmployeeId = dto.EmployeeId,
            LocationId = dto.LocationId,
            CarId = dto.CarId,
            ClockIn = dto.ClockIn,
            ClockOut = dto.ClockOut,
            Note = dto.Note
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
            Note = entry.Note
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateTimeEntryDto dto)
    {
        var entry = await _db.TimeEntries.FindAsync(id);
        if (entry == null) return NotFound();

        entry.CarId = dto.CarId;
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

        _db.TimeEntries.Remove(entry);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
