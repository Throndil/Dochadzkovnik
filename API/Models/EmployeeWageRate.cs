namespace API.Models;

/// <summary>
/// An effective-dated hourly wage rate for an employee. The rate history is
/// the source of truth for pricing shifts: a <see cref="TimeEntry"/> is priced
/// at the rate whose <see cref="EffectiveFrom"/> is the latest on-or-before the
/// shift's date. Adding or changing a rate recomputes the
/// <see cref="TimeEntry.WageAtTime"/> cache on affected entries — see
/// <c>WageService</c>. This replaces the old "remember to backfill" footgun:
/// setting a rate from a date always reprices the shifts from that date.
///
/// Admin-only data; MUST NOT appear in any /api/kiosk/* response DTO.
/// </summary>
public class EmployeeWageRate
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    /// <summary>EUR per hour. Non-negative.</summary>
    public decimal RatePerHour { get; set; }

    /// <summary>Date (UTC; time component ignored) from which this rate applies.</summary>
    public DateTime EffectiveFrom { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>JWT username of the admin who set the rate. Audit only.</summary>
    public string? CreatedBy { get; set; }

    public Employee Employee { get; set; } = null!;
}
