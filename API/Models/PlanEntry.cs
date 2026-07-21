namespace API.Models;

/// <summary>
/// One planner bar (Plánovač, behind the "Planner" flag): an employee is
/// planned to a pracovisko — or marked absent — for an inclusive date range.
/// Multi-day bars render as one entry; overlaps are allowed (the grid stacks
/// them into lanes). Admin-only; the kiosk does not read this (yet).
/// </summary>
public class PlanEntry
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    /// <summary>"praca" (needs LocationId) | "dovolenka" | "pn" | "volno".</summary>
    public string Type { get; set; } = "praca";

    /// <summary>Pracovisko for Type == "praca"; null on absences.</summary>
    public int? LocationId { get; set; }

    /// <summary>First planned day (date only, midnight).</summary>
    public DateTime StartDate { get; set; }

    /// <summary>Last planned day, INCLUSIVE (date only, midnight).</summary>
    public DateTime EndDate { get; set; }

    public string? Note { get; set; }

    /// <summary>JWT username of the admin who created/last edited the bar.</summary>
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Employee Employee { get; set; } = null!;
    public Location? Location { get; set; }
}
