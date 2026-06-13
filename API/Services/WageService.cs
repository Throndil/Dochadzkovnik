using API.Data;
using API.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services;

public interface IWageService
{
    /// <summary>
    /// Set (or change) an employee's hourly rate effective from a date, then
    /// reprice their shifts from the rate history. Returns the number of
    /// <see cref="TimeEntry"/> rows whose <see cref="TimeEntry.WageAtTime"/>
    /// changed.
    ///
    /// <paramref name="effectiveFrom"/> null → smart default: the employee's
    /// start date when no rate exists yet (so every shift they ever clocked is
    /// covered), otherwise today (a forward-looking raise).
    /// <paramref name="rate"/> null → clears the rate history; shifts with no
    /// effective rate fall back to 0.
    /// </summary>
    Task<int> SetWageAsync(int employeeId, decimal? rate, DateTime? effectiveFrom, string? actor);
}

/// <summary>
/// Single source of truth for hourly-wage changes. Used by both the Mzdy
/// payroll controller and the employee-detail save, so a rate set anywhere
/// behaves identically: it lands in the rate history and reprices shifts.
/// See PAYROLL_AND_PNL_PLAN.md.
/// </summary>
public class WageService : IWageService
{
    private readonly AppDbContext _db;

    public WageService(AppDbContext db) => _db = db;

    public async Task<int> SetWageAsync(int employeeId, decimal? rate, DateTime? effectiveFrom, string? actor)
    {
        var emp = await _db.Employees
            .Include(e => e.WageRates)
            .FirstOrDefaultAsync(e => e.Id == employeeId)
            ?? throw new InvalidOperationException($"Employee {employeeId} not found.");

        if (rate is decimal r && r < 0m)
            throw new ArgumentOutOfRangeException(nameof(rate), "Rate must be non-negative.");

        if (rate.HasValue)
        {
            var hadRate = emp.WageRates.Count > 0;
            var fromDate = DateTime.SpecifyKind(
                (effectiveFrom?.Date) ?? (hadRate ? DateTime.UtcNow.Date : emp.CreatedAt.Date),
                DateTimeKind.Utc);

            // Upsert: one rate row per (employee, effective date). Re-setting the
            // same date overwrites rather than stacking duplicate rows.
            var existing = emp.WageRates.FirstOrDefault(w => w.EffectiveFrom.Date == fromDate.Date);
            if (existing != null)
            {
                existing.RatePerHour = rate.Value;
                existing.CreatedBy = actor;
                existing.CreatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.EmployeeWageRates.Add(new EmployeeWageRate
                {
                    EmployeeId = employeeId,
                    RatePerHour = rate.Value,
                    EffectiveFrom = fromDate,
                    CreatedBy = actor
                });
            }
            await _db.SaveChangesAsync();
        }
        else if (emp.WageRates.Count > 0)
        {
            // Clearing: drop the history so nothing prices the shifts and the
            // current-rate cache (Employee.HourlyWage) goes back to null.
            _db.EmployeeWageRates.RemoveRange(emp.WageRates);
            await _db.SaveChangesAsync();
        }

        return await RecomputeSnapshotsAsync(employeeId);
    }

    /// <summary>
    /// Reprice every TimeEntry: WageAtTime = the rate effective on the shift's
    /// ClockIn date (0 when no rate covers it). Also refresh the
    /// Employee.HourlyWage cache to the rate in effect today — that's what the
    /// kiosk clock-in snapshots onto brand-new shifts and what the UI shows.
    /// </summary>
    private async Task<int> RecomputeSnapshotsAsync(int employeeId)
    {
        var rates = await _db.EmployeeWageRates
            .Where(w => w.EmployeeId == employeeId)
            .OrderBy(w => w.EffectiveFrom)
            .ToListAsync();

        var entries = await _db.TimeEntries
            .Where(t => t.EmployeeId == employeeId)
            .ToListAsync();

        var changed = 0;
        foreach (var t in entries)
        {
            // rates are ascending by EffectiveFrom, so the last one on-or-before
            // the shift date is the applicable rate.
            var applicable = rates
                .Where(w => w.EffectiveFrom.Date <= t.ClockIn.Date)
                .Select(w => (decimal?)w.RatePerHour)
                .LastOrDefault() ?? 0m;
            if (t.WageAtTime != applicable)
            {
                t.WageAtTime = applicable;
                changed++;
            }
        }

        var today = DateTime.UtcNow.Date;
        var emp = await _db.Employees.FirstAsync(e => e.Id == employeeId);
        emp.HourlyWage = rates
            .Where(w => w.EffectiveFrom.Date <= today)
            .Select(w => (decimal?)w.RatePerHour)
            .LastOrDefault();

        await _db.SaveChangesAsync();
        return changed;
    }
}
