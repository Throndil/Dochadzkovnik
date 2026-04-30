namespace API.Models;

public class NotificationLog
{
    public int Id { get; set; }
    public int? EmployeeId { get; set; }
    public string Channel { get; set; } = string.Empty; // "Push", "WhatsApp"
    public string TriggerType { get; set; } = string.Empty; // "NoActivity48h", "ManagerSummary", "Test"
    public string Body { get; set; } = string.Empty;
    public DateOnly TriggerDate { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Sent"; // "Sent", "Failed", "Skipped", "NoSubscription"
    public string? ProviderMessageId { get; set; }
    public string? ErrorMessage { get; set; }
}
