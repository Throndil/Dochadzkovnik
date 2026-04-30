namespace API.Models;

public class NotificationConfig
{
    public int Id { get; set; } = 1; // Always 1
    public bool NoActivity48hEnabled { get; set; } = false;
    public TimeSpan NoActivity48hTime { get; set; } = TimeSpan.FromHours(18);
    public bool WorkingDaysOnly { get; set; } = true;
    public bool ManagerSummaryEnabled { get; set; } = false;
    public int? ManagerSummaryEmployeeId { get; set; }
    public DateTime? LastTickAt { get; set; }
    public string VapidPublicKey { get; set; } = string.Empty;
    public string VapidPrivateKey { get; set; } = string.Empty;
    public string VapidSubject { get; set; } = string.Empty;
}
