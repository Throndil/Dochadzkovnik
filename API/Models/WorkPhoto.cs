namespace API.Models;

/// <summary>
/// A standalone proof-of-work photo uploaded by a worker via the kiosk.
/// Not tied to a specific time entry — used when a worker wants to document
/// their presence at a location without (or in addition to) logging hours.
/// </summary>
public class WorkPhoto
{
    public int Id { get; set; }
    public int? EmployeeId { get; set; }   // null = admin-uploaded photo
    public int LocationId { get; set; }
    public string PhotoUrl { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Employee? Employee { get; set; }
    public Location Location { get; set; } = null!;
}
