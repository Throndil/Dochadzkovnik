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
    public bool NotificationsEnabled { get; set; } = true;
    public bool WhatsAppEnabled { get; set; } = false;
    public string? WhatsAppNumber { get; set; }
    /// <summary>Set when the worker explicitly declines push notifications from the kiosk banner.</summary>
    public string? NotificationsDeclineReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TimeEntry> TimeEntries { get; set; } = [];
}
