namespace API.Models;

/// <summary>
/// One company fuel card (F6 — the customer has 6). Optionally assigned to
/// the employee who currently holds it; unassigned cards are fine because
/// some holders are not in the system yet. Managed on /admin/palivove-karty.
/// </summary>
public class FuelCard
{
    public int Id { get; set; }

    /// <summary>Display label, e.g. "Karta 1" or the card number.</summary>
    public string Label { get; set; } = string.Empty;

    public string? Note { get; set; }

    /// <summary>Current holder. Null = unassigned / holder not in the system.</summary>
    public int? EmployeeId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Employee? Employee { get; set; }
}
