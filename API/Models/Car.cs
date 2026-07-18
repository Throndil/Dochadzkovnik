namespace API.Models;

public class Car
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LicensePlate { get; set; }
    public string? PhotoUrl { get; set; }
    /// <summary>"profistav" | "stroje" — which division the vehicle belongs
    /// to (Fáza F). Its costs backtrack + Commander mapping follow this.</summary>
    public string Division { get; set; } = "profistav";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TimeEntry> TimeEntries { get; set; } = [];
}
