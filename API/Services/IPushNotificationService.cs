namespace API.Services;

public class PushSendResult
{
    public bool Success { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsGoneStale { get; set; } // 410 Gone from push service
}

public interface IPushNotificationService
{
    Task<PushSendResult> SendAsync(
        Models.PushSubscription sub,
        string title,
        string body,
        string? clickUrl,
        CancellationToken ct = default);
}
