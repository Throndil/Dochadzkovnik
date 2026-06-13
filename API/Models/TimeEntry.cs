namespace API.Models;

public class TimeEntry
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public int LocationId { get; set; }
    public DateTime ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public string? Note { get; set; }
    public string? PhotoUrl { get; set; }

    /// <summary>
    /// True when the worker explicitly picked "Pokračovať bez dôkazu" on the
    /// kiosk proof-of-work step. Lets the admin Záznamy dochádzky table
    /// distinguish "skipped on purpose" from the historical "forgot" state
    /// (no photo, no diary, flag false). See PROOF_OF_WORK_UX_PLAN.md §(d).
    /// </summary>
    public bool ProofOfWorkSkipped { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int? CarId { get; set; }

    /// <summary>
    /// Snapshot of <see cref="Employee.HourlyWage"/> at the moment this entry
    /// was inserted (or last explicitly updated by an admin). Inflation-
    /// protected: changing an employee's wage tomorrow does NOT rewrite the
    /// wage on past entries. Computed payroll always uses this column, never
    /// the live <c>Employee.HourlyWage</c>. See PAYROLL_AND_PNL_PLAN.md
    /// §design decision (a). Admin-only data; never leak to /api/kiosk/*.
    /// </summary>
    public decimal WageAtTime { get; set; } = 0m;

    public Employee Employee { get; set; } = null!;
    public Location Location { get; set; } = null!;
    public Car? Car { get; set; }
}
