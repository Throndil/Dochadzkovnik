using System.Globalization;
using System.Text;
using API.Data;
using API.DTOs;
using API.Filters;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// Mzdy (payroll) admin controller — see PAYROLL_AND_PNL_PLAN.md.
/// All endpoints behind the PayrollAndPnL feature flag. Wages are
/// admin-only data; the kiosk surface uses different DTOs that don't
/// expose any of the fields surfaced here.
/// </summary>
[ApiController]
[Route("api/payroll")]
[Authorize]
[RequireFeatureOrSuperAdmin("PayrollAndPnL")]
public class PayrollController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPayrollExcelExportService _xlsx;
    private readonly IWageService _wage;
    private readonly ILogger<PayrollController> _log;

    // Shared client for pulling photo thumbnails to embed in the Excel export.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Rewrites a Cloudinary image URL to request a small JPEG thumbnail
    /// (≤180×120, auto-quality) for embedding. Non-Cloudinary URLs pass through.
    /// </summary>
    private static string ThumbUrl(string url)
    {
        const string marker = "/upload/";
        var i = url.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return url;
        return url.Insert(i + marker.Length, "w_180,h_120,c_limit,q_auto,f_jpg/");
    }

    /// <summary>Best-effort thumbnail download — returns null on any failure so the export never breaks.</summary>
    private static async Task<byte[]?> TryFetchThumbnailAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _http.GetByteArrayAsync(ThumbUrl(url), cts.Token);
        }
        catch
        {
            return null;
        }
    }

    public PayrollController(AppDbContext db, IPayrollExcelExportService xlsx, IWageService wage, ILogger<PayrollController> log)
    {
        _db = db;
        _xlsx = xlsx;
        _wage = wage;
        _log = log;
    }

    /// <summary>
    /// GET /api/payroll/monthly?month=YYYY-MM
    /// or  /api/payroll/monthly?from=YYYY-MM-DD&amp;to=YYYY-MM-DD (to inclusive)
    /// One row per employee with at least one TimeEntry OR EmployeeAdvance
    /// in the selected period. Includes inactive employees with historical
    /// activity (greyed out client-side via IsActive flag).
    /// </summary>
    [HttpGet("monthly")]
    public async Task<ActionResult<PayrollMonthlyDto>> Monthly(
        [FromQuery] string? month,
        [FromQuery(Name = "from")] string? fromStr,
        [FromQuery(Name = "to")] string? toStr)
    {
        if (!TryParsePeriod(month, fromStr, toStr, out var from, out var toExcl, out var label))
            return BadRequest("Zadajte mesiac vo formáte YYYY-MM alebo rozsah from/to vo formáte YYYY-MM-DD.");

        var data = await BuildMonthlyAsync(label, from, toExcl);
        return data;
    }

    /// <summary>
    /// GET /api/payroll/monthly/export?month=YYYY-MM (or ?from=&amp;to= range)
    /// XLSX of the same shape as /monthly. Single-sheet, Slovak headers.
    /// </summary>
    [HttpGet("monthly/export")]
    public async Task<IActionResult> ExportMonthly(
        [FromQuery] string? month,
        [FromQuery(Name = "from")] string? fromStr,
        [FromQuery(Name = "to")] string? toStr)
    {
        if (!TryParsePeriod(month, fromStr, toStr, out var from, out var toExcl, out var label))
            return BadRequest("Zadajte mesiac vo formáte YYYY-MM alebo rozsah from/to vo formáte YYYY-MM-DD.");

        var data = await BuildMonthlyAsync(label, from, toExcl);
        var title = !string.IsNullOrWhiteSpace(month)
            ? month
            : $"{from:dd.MM.yyyy} – {toExcl.AddDays(-1):dd.MM.yyyy}";
        var bytes = _xlsx.BuildMonthlySummary(title, data);
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Mzdy_{label}.xlsx");
    }

    /// <summary>
    /// GET /api/payroll/employee/{id}/export?month=YYYY-MM (or ?from=&amp;to= range)
    /// The comprehensive per-worker workbook — locations, daily breakdown,
    /// photos (as hyperlinks), cars, advances. Customer's "monthly paycheck".
    /// </summary>
    [HttpGet("employee/{id}/export")]
    public async Task<IActionResult> ExportEmployee(
        int id,
        [FromQuery] string? month,
        [FromQuery(Name = "from")] string? fromStr,
        [FromQuery(Name = "to")] string? toStr)
    {
        if (!TryParsePeriod(month, fromStr, toStr, out var from, out var toExcl, out var label))
            return BadRequest("Zadajte mesiac vo formáte YYYY-MM alebo rozsah from/to vo formáte YYYY-MM-DD.");

        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        // ── Time entries with location/car joins ──
        var entries = await _db.TimeEntries
            .Include(t => t.Location)
            .Include(t => t.Car)
            .Where(t => t.EmployeeId == id
                     && t.ClockIn >= from
                     && t.ClockIn <  toExcl
                     && t.ClockOut != null)
            .OrderBy(t => t.ClockIn)
            .ToListAsync();

        var days = entries.Select(t =>
        {
            var hours = (decimal)(t.ClockOut!.Value - t.ClockIn).TotalHours;
            var earnings = Math.Round(hours * t.WageAtTime, 2, MidpointRounding.AwayFromZero);
            var hasDiary = _db.WorkDiaries.Any(d => d.TimeEntryId == t.Id);
            return new EmployeeDayRow(
                t.ClockIn.Date,
                t.Location.Name,
                t.Car?.Name,
                Math.Round(hours, 2, MidpointRounding.AwayFromZero),
                t.WageAtTime,
                earnings,
                hasDiary,
                t.PhotoUrl,
                t.Note);
        }).ToList();

        var locations = days
            .GroupBy(d => d.LocationName)
            .Select(g => new EmployeeLocationRow(
                g.Key,
                g.Sum(x => x.Hours),
                g.Sum(x => x.Earnings),
                g.Count()))
            .ToList();

        var cars = days
            .Where(d => d.CarName != null)
            .GroupBy(d => d.CarName!)
            .Select(g => new EmployeeCarRow(
                g.Key,
                entries.FirstOrDefault(e => e.Car?.Name == g.Key)?.Car?.LicensePlate,
                g.Sum(x => x.Hours),
                g.Count()))
            .ToList();

        // ── Photos: union of TimeEntry photos and WorkPhotos ──
        var photos = new List<EmployeePhotoRow>();
        foreach (var t in entries.Where(x => !string.IsNullOrEmpty(x.PhotoUrl)))
        {
            photos.Add(new EmployeePhotoRow(
                t.ClockIn,
                t.Location.Name,
                t.PhotoUrl!,
                "Šichta",
                t.Note));
        }
        var workPhotos = await _db.WorkPhotos
            .Include(p => p.Location)
            .Where(p => p.EmployeeId == id
                     && p.CreatedAt >= from
                     && p.CreatedAt <  toExcl)
            .ToListAsync();
        foreach (var p in workPhotos)
        {
            photos.Add(new EmployeePhotoRow(
                p.CreatedAt,
                p.Location.Name,
                p.PhotoUrl,
                "Foto pracoviska",
                p.Note));
        }

        // ── Advances in month ──
        var advances = await _db.EmployeeAdvances
            .Where(a => a.EmployeeId == id && a.Date >= from && a.Date < toExcl)
            .OrderBy(a => a.Date)
            .ToListAsync();
        var advanceDtos = advances.Select(a => new EmployeeAdvanceDto
        {
            Id         = a.Id,
            EmployeeId = a.EmployeeId,
            Date       = a.Date,
            Amount     = a.Amount,
            Note       = a.Note,
            CreatedBy  = a.CreatedBy,
            CreatedAt  = a.CreatedAt
        }).ToList();

        // Download thumbnails to embed in the Fotky sheet. Best-effort and in
        // parallel; any failure leaves ThumbnailBytes null and the row keeps
        // just its hyperlink.
        await Task.WhenAll(photos.Select(async p => p.ThumbnailBytes = await TryFetchThumbnailAsync(p.PhotoUrl)));

        var data = new EmployeeReportData(
            EmployeeId: emp.Id,
            FullName: $"{emp.FirstName} {emp.LastName}",
            Month: label,
            PeriodFrom: from,
            PeriodTo: toExcl,
            CurrentHourlyWage: emp.HourlyWage,
            Days: days,
            Locations: locations,
            Cars: cars,
            Photos: photos,
            Advances: advanceDtos);

        var bytes = _xlsx.BuildEmployeeReport(data);
        var safeName = $"{emp.FirstName}_{emp.LastName}"
            .Replace(' ', '_')
            .Normalize(NormalizationForm.FormD);
        var ascii = new string(safeName.Where(c =>
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Mzda_{ascii}_{label}.xlsx");
    }

    /// <summary>
    /// POST /api/payroll/employee/{id}/set-wage
    /// Body: { rate: number | null, applyFrom?: "YYYY-MM-DD" }
    ///
    /// Records the rate in the effective-dated wage history (see
    /// <c>WageService</c>) and reprices every shift from the rate in effect on
    /// its date. <c>applyFrom</c> is the effective date — e.g. a promotion
    /// ("Janko's new rate of 6,80 applies from 15.05."). When omitted, the
    /// service applies the rate from the employee's start date if they have no
    /// rate yet (so all their shifts are covered), otherwise from today.
    ///
    /// rate NULL = clear the rate history; shifts with no effective rate
    /// price at 0. Returns the number of shifts whose price changed.
    /// </summary>
    [HttpPost("employee/{id}/set-wage")]
    public async Task<ActionResult<int>> SetWage(int id, [FromBody] SetWageRequest body)
    {
        var exists = await _db.Employees.AnyAsync(e => e.Id == id);
        if (!exists) return NotFound();

        // The rate goes into the effective-dated history (WageService), which
        // reprices every shift from the rate in effect on its date. ApplyFrom
        // is the effective date; null lets the service pick a sensible default
        // (the employee's start date for a first rate, else today for a raise).
        try
        {
            var repriced = await _wage.SetWageAsync(id, body.Rate, body.ApplyFrom, User.Identity?.Name);
            _log.LogInformation(
                "[Payroll] SetWage employee={Id} rate={Rate} effectiveFrom={From} → {N} shifts repriced",
                id, body.Rate, body.ApplyFrom, repriced);
            return Ok(repriced);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest("Sadzba musí byť kladná.");
        }
    }

    public sealed class SetWageRequest
    {
        public decimal? Rate { get; set; }
        public DateTime? ApplyFrom { get; set; }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private async Task<PayrollMonthlyDto> BuildMonthlyAsync(string periodLabel, DateTime from, DateTime toExcl)
    {
        // Pull every employee with activity in the window — TimeEntries with
        // ClockIn in [from, toExcl) OR EmployeeAdvances with Date in window.
        var entryEmpIds = await _db.TimeEntries
            .Where(t => t.ClockIn >= from && t.ClockIn < toExcl && t.ClockOut != null)
            .Select(t => t.EmployeeId)
            .Distinct()
            .ToListAsync();
        var advanceEmpIds = await _db.EmployeeAdvances
            .Where(a => a.Date >= from && a.Date < toExcl)
            .Select(a => a.EmployeeId)
            .Distinct()
            .ToListAsync();
        var allIds = entryEmpIds.Concat(advanceEmpIds).Distinct().ToList();

        var employees = await _db.Employees
            .Where(e => allIds.Contains(e.Id))
            .ToListAsync();

        // Pull all closed entries in the window and aggregate in memory.
        // Data volume is tiny (one customer, dozens of workers, hundreds of
        // entries per month) so in-memory grouping avoids EF provider
        // differences on DateDiff functions. Decimals stay decimal so cent-
        // level rounding on weighted averages is exact.
        var rawEntries = await _db.TimeEntries
            .Where(t => t.ClockIn >= from && t.ClockIn < toExcl && t.ClockOut != null)
            .Select(t => new { t.EmployeeId, t.ClockIn, t.ClockOut, t.WageAtTime })
            .ToListAsync();

        var entryAggregates = rawEntries
            .GroupBy(t => t.EmployeeId)
            .Select(g => new
            {
                EmployeeId        = g.Key,
                Hours             = g.Sum(t => (decimal)(t.ClockOut!.Value - t.ClockIn).TotalHours),
                WeightedNumerator = g.Sum(t => (decimal)(t.ClockOut!.Value - t.ClockIn).TotalHours * t.WageAtTime),
                AnyNonZeroWage    = g.Any(t => t.WageAtTime > 0m)
            })
            .ToList();

        var advanceAggregates = await _db.EmployeeAdvances
            .Where(a => a.Date >= from && a.Date < toExcl)
            .GroupBy(a => a.EmployeeId)
            .Select(g => new
            {
                EmployeeId = g.Key,
                Total = g.Sum(a => a.Amount)
            })
            .ToListAsync();

        var rows = new List<PayrollRowDto>();
        foreach (var emp in employees.OrderBy(e => e.LastName).ThenBy(e => e.FirstName))
        {
            var ea = entryAggregates.FirstOrDefault(x => x.EmployeeId == emp.Id);
            var aa = advanceAggregates.FirstOrDefault(x => x.EmployeeId == emp.Id);

            var hours = ea?.Hours ?? 0m;
            var avg   = ea == null || ea.Hours == 0m
                ? (decimal?)null
                : Math.Round(ea.WeightedNumerator / ea.Hours, 4, MidpointRounding.AwayFromZero);
            var gross = avg.HasValue ? Math.Round(hours * avg.Value, 2, MidpointRounding.AwayFromZero) : 0m;
            var adv   = aa?.Total ?? 0m;

            rows.Add(new PayrollRowDto
            {
                EmployeeId            = emp.Id,
                FirstName             = emp.FirstName,
                LastName              = emp.LastName,
                IsActive              = emp.IsActive,
                HoursWorked           = Math.Round(hours, 2, MidpointRounding.AwayFromZero),
                HourlyWageSnapshotAvg = avg,
                HourlyWageCurrent     = emp.HourlyWage,
                WageMissing           = ea != null && !ea.AnyNonZeroWage,
                AdvancesTotal         = adv,
                Gross                 = gross,
                Payout                = gross - adv
            });
        }

        var totals = new PayrollRowDto
        {
            HoursWorked   = rows.Sum(r => r.HoursWorked),
            AdvancesTotal = rows.Sum(r => r.AdvancesTotal),
            Gross         = rows.Sum(r => r.Gross),
            Payout        = rows.Sum(r => r.Payout)
        };

        return new PayrollMonthlyDto
        {
            Month  = periodLabel,
            Rows   = rows,
            Totals = totals
        };
    }

    /// <summary>
    /// Resolve the selected period: <c>month</c> (YYYY-MM) takes precedence;
    /// otherwise an explicit <c>from</c>/<c>to</c> range (YYYY-MM-DD, <c>to</c>
    /// inclusive — converted to an exclusive upper bound internally).
    /// <c>label</c> is the period identifier used in DTOs and filenames
    /// ("2026-05" or "2026-05-04_2026-05-10").
    /// </summary>
    private static bool TryParsePeriod(
        string? month, string? fromStr, string? toStr,
        out DateTime from, out DateTime toExcl, out string label)
    {
        if (!string.IsNullOrWhiteSpace(month))
        {
            label = month;
            return TryParseMonth(month, out from, out toExcl);
        }

        from = default;
        toExcl = default;
        label = string.Empty;
        if (string.IsNullOrWhiteSpace(fromStr) || string.IsNullOrWhiteSpace(toStr)) return false;
        if (!DateTime.TryParseExact(fromStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var f)) return false;
        if (!DateTime.TryParseExact(toStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t)) return false;
        if (t < f) return false;
        from   = DateTime.SpecifyKind(f.Date, DateTimeKind.Utc);
        toExcl = DateTime.SpecifyKind(t.Date.AddDays(1), DateTimeKind.Utc);
        label  = $"{fromStr}_{toStr}";
        return true;
    }

    /// <summary>
    /// Parse YYYY-MM into the [first-of-month .. first-of-next-month) range.
    /// </summary>
    private static bool TryParseMonth(string? month, out DateTime from, out DateTime toExcl)
    {
        from = default;
        toExcl = default;
        if (string.IsNullOrWhiteSpace(month)) return false;
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var first)) return false;
        from   = new DateTime(first.Year, first.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        toExcl = from.AddMonths(1);
        return true;
    }
}
