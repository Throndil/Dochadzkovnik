using System.Globalization;
using System.Text;
using API.Data;
using API.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    public ReportsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("daily")]
    public async Task<ActionResult<DailyReportDto>> GetDaily([FromQuery] DateTime? date)
    {
        var d = (date ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz)).Date;

        var entries = await _db.TimeEntries
            .Include(t => t.Employee)
            .Include(t => t.Location)
            .Where(t => t.ClockIn.Date == d)
            .OrderBy(t => t.ClockIn)
            .ToListAsync();

        return new DailyReportDto
        {
            Date = d,
            Entries = entries.Select(t => new DailyReportEntryDto
            {
                EmployeeName = $"{t.Employee.FirstName} {t.Employee.LastName}",
                LocationName = t.Location.Name,
                ClockIn = t.ClockIn,
                ClockOut = t.ClockOut,
                HoursWorked = t.ClockOut.HasValue ? (t.ClockOut.Value - t.ClockIn).TotalHours : null
            }).ToList(),
            TotalHours = entries
                .Where(t => t.ClockOut.HasValue)
                .Sum(t => (t.ClockOut!.Value - t.ClockIn).TotalHours)
        };
    }

    [HttpGet("summary")]
    public async Task<ActionResult<List<TimeEntryDto>>> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? employeeId,
        [FromQuery] int? locationId)
    {
        var query = _db.TimeEntries
            .Include(t => t.Employee)
            .Include(t => t.Location)
            .AsQueryable();

        if (from.HasValue) query = query.Where(t => t.ClockIn >= from.Value);
        if (to.HasValue) query = query.Where(t => t.ClockIn < to.Value.AddDays(1));
        if (employeeId.HasValue) query = query.Where(t => t.EmployeeId == employeeId);
        if (locationId.HasValue) query = query.Where(t => t.LocationId == locationId);

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
                ClockIn = t.ClockIn,
                ClockOut = t.ClockOut,
                HoursWorked = t.ClockOut.HasValue
                    ? (t.ClockOut.Value - t.ClockIn).TotalHours
                    : null,
                Note = t.Note
            })
            .ToListAsync();
    }

    [HttpGet("export/csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? employeeId,
        [FromQuery] int? locationId)
    {
        var query = _db.TimeEntries
            .Include(t => t.Employee)
            .Include(t => t.Location)
            .AsQueryable();

        if (from.HasValue) query = query.Where(t => t.ClockIn >= from.Value);
        if (to.HasValue) query = query.Where(t => t.ClockIn < to.Value.AddDays(1));
        if (employeeId.HasValue) query = query.Where(t => t.EmployeeId == employeeId);
        if (locationId.HasValue) query = query.Where(t => t.LocationId == locationId);

        var entries = await query.OrderBy(t => t.ClockIn).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Zamestnanec,Pracovisko,Príchod,Odchod,Hodiny,Poznámka");

        foreach (var t in entries)
        {
            var hours = t.ClockOut.HasValue
                ? (t.ClockOut.Value - t.ClockIn).TotalHours.ToString("F2", CultureInfo.InvariantCulture)
                : "";
            var name = $"{t.Employee.FirstName} {t.Employee.LastName}";
            var clockIn = t.ClockIn.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var clockOut = t.ClockOut?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
            var note = t.Note?.Replace("\"", "\"\"") ?? "";

            sb.AppendLine($"\"{name}\",\"{t.Location.Name}\",\"{clockIn}\",\"{clockOut}\",{hours},\"{note}\"");
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "time-report.csv");
    }
}
