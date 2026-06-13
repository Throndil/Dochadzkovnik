namespace API.Models;

/// <summary>
/// A cash advance / záloha paid to a worker, subtracted from their monthly
/// payout. Admin-only data per PAYROLL_AND_PNL_PLAN.md §design decision (e);
/// MUST NOT appear in any /api/kiosk/* response DTO. Negative amounts are out
/// of scope in V1 — if the customer wants refunds / clawbacks later, allow
/// negative <see cref="Amount"/> or add a Type enum then.
/// </summary>
public class EmployeeAdvance
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    /// <summary>The date the advance was paid (not the date it was recorded).</summary>
    public DateTime Date { get; set; }

    /// <summary>EUR. Positive only in V1.</summary>
    public decimal Amount { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>JWT username of the admin who recorded the advance. Audit only.</summary>
    public string? CreatedBy { get; set; }

    public Employee Employee { get; set; } = null!;
}
