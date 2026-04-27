namespace API.Models;

public class PushSubscription
{
    public int Id { get; set; }
    public int? EmployeeId { get; set; }
    public string Endpoint { get; set; } = string.Empty; // unique
    public string P256dhKey { get; set; } = string.Empty;
    public string AuthKey { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}
