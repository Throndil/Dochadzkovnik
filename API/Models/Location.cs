namespace API.Models;

public class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Contract value (Príjem / zmluvná hodnota) in EUR. NULL = no contract
    /// recorded yet — the P&amp;L card shows the revenue row as "—" and hides
    /// the profit total. One number per site in V1; promote to a
    /// LocationContract table if the customer asks for multiple contracts.
    /// See PAYROLL_AND_PNL_PLAN.md §design decision (f). Admin-only data;
    /// MUST NOT appear in any /api/kiosk/* response DTO.
    /// </summary>
    public decimal? ContractValue { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TimeEntry> TimeEntries { get; set; } = [];
    public ICollection<MaterialUsage> MaterialUsages { get; set; } = [];
}
