using System.Text.Json;
using API.Data;
using API.Models;
using Microsoft.EntityFrameworkCore;
using WebPush;
// Alias the unqualified `PushSubscription` token to our EF entity so it doesn't collide
// with `WebPush.PushSubscription` (the library DTO). The library type is fully-qualified
// at the one place it's constructed below.
using PushSubscription = API.Models.PushSubscription;

namespace API.Services;

public class WebPushService : IPushNotificationService
{
    private readonly ILogger<WebPushService> _logger;
    private readonly AppDbContext _db;
    private NotificationConfig? _cachedConfig;
    private DateTime _configCachedAt = DateTime.MinValue;

    public WebPushService(ILogger<WebPushService> logger, AppDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    private async Task<NotificationConfig?> GetConfigAsync(CancellationToken ct)
    {
        // Cache for 1 minute
        if (_cachedConfig != null && DateTime.UtcNow.Subtract(_configCachedAt).TotalSeconds < 60)
            return _cachedConfig;

        _cachedConfig = await _db.NotificationConfigs.FirstOrDefaultAsync(c => c.Id == 1, ct);
        _configCachedAt = DateTime.UtcNow;
        return _cachedConfig;
    }

    public async Task<PushSendResult> SendAsync(
        PushSubscription sub,
        string title,
        string body,
        string? clickUrl,
        CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);
        if (config == null || string.IsNullOrEmpty(config.VapidPublicKey) || string.IsNullOrEmpty(config.VapidPrivateKey))
        {
            _logger.LogWarning("VAPID keys not configured");
            return new PushSendResult { Success = false, ErrorMessage = "VAPID keys not configured" };
        }

        try
        {
            var payload = new
            {
                title,
                body,
                clickUrl
            };

            var webPushClient = new WebPushClient();
            var vapidDetails = new VapidDetails(
                config.VapidSubject,
                config.VapidPublicKey,
                config.VapidPrivateKey);

            var pushSubscription = new WebPush.PushSubscription(
                sub.Endpoint,
                sub.P256dhKey,
                sub.AuthKey);

            var jsonPayload = JsonSerializer.Serialize(payload);

            await webPushClient.SendNotificationAsync(
                pushSubscription,
                jsonPayload,
                vapidDetails);

            _logger.LogInformation("Push sent to endpoint {endpoint}", sub.Endpoint);
            sub.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new PushSendResult { Success = true };
        }
        catch (WebPushException ex)
        {
            _logger.LogError(ex, "WebPush error: {message}", ex.Message);

            // 404 = subscription no longer valid (likely uninstalled)
            // 410 = subscription expired or gone stale
            if (ex.StatusCode == System.Net.HttpStatusCode.NotFound ||
                ex.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                return new PushSendResult
                {
                    Success = false,
                    IsGoneStale = true,
                    ErrorMessage = $"HTTP {ex.StatusCode}: subscription invalid"
                };
            }

            return new PushSendResult
            {
                Success = false,
                ErrorMessage = $"HTTP {ex.StatusCode}: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending push: {message}", ex.Message);
            return new PushSendResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
