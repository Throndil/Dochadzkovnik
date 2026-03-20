namespace API.Models;

public class TimeEntry
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public int LocationId { get; set; }
    public DateTime ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Employee Employee { get; set; } = null!;
    public Location Location { get; set; } = null!;
}
