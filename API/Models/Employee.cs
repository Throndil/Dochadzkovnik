namespace API.Models;

public class Employee
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Pin { get; set; } = string.Empty; // Hashed
    public string? PinPlain { get; set; }            // Stored for manager view
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Company division the employee belongs to: "profistav" (stavby)
    /// | "stroje". Drives the division-scoped Mzdy view; the driver's wages
    /// land under AZ Stroje simply by assigning him there.</summary>
    public string Division { get; set; } = "profistav";
    /// <summary>Free-text job position (F6): "šofér", "murár"… Kept free-text
    /// because the customer's positions may be added or changed over time.</summary>
    public string? Position { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public bool WhatsAppEnabled { get; set; } = false;
    public string? WhatsAppNumber { get; set; }
    /// <summary>Set when the worker explicitly declines push notifications from the kiosk banner.</summary>
    public string? NotificationsDeclineReason { get; set; }
    /// <summary>
    /// Hourly wage in EUR. NULL means "not set yet" — the Mzdy view warns
    /// the admin in amber and any TimeEntry inserted while NULL snapshots
    /// WageAtTime = 0. See PAYROLL_AND_PNL_PLAN.md §design decision (a).
    /// Admin-only data; MUST NOT appear in any /api/kiosk/* response DTO.
    /// </summary>
    public decimal? HourlyWage { get; set; }

    /// <summary>
    /// Employer contributions (odvody) as a % of gross wage, set manually per
    /// worker on the Mzdy page. NULL = not set → counts as 0. Guidance shown
    /// in the UI: TPP 36,2 % (25,2 % sociálne + 11 % zdravotné, 2026),
    /// ZŤP 30,7 %; živnostníci 0. Read LIVE (no per-entry snapshot) — same
    /// ponytail as the Odvody page rates.
    /// </summary>
    public decimal? OdvodyPct { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TimeEntry> TimeEntries { get; set; } = [];
    public ICollection<EmployeeAdvance> Advances { get; set; } = [];
    public ICollection<EmployeeWageRate> WageRates { get; set; } = [];
}
