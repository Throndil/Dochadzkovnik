using API.Data;
using API.Models;
using API.Services;
using Microsoft.EntityFrameworkCore;

namespace API.BackgroundServices;

public class NotificationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationBackgroundService> _logger;
    private static readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    public NotificationBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationBackgroundService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Tick(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in notification background service tick");
            }

            await Task.Delay(60000, stoppingToken); // 60 second tick
        }

        _logger.LogInformation("NotificationBackgroundService stopping");
    }

    private async Task Tick(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        // Read config
        var config = await db.NotificationConfigs
            .FirstOrDefaultAsync(c => c.Id == 1, ct);

        if (config == null)
        {
            _logger.LogWarning("NotificationConfig not found");
            return;
        }

        if (!config.NoActivity48hEnabled)
            return; // Feature disabled

        // Convert now to Bratislava time
        var nowBratislava = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);
        var todayBratislava = DateOnly.FromDateTime(nowBratislava);

        // Check working days
        if (config.WorkingDaysOnly && (nowBratislava.DayOfWeek == DayOfWeek.Saturday || nowBratislava.DayOfWeek == DayOfWeek.Sunday))
        {
            _logger.LogDebug("Skipping notification check — weekend and WorkingDaysOnly is true");
            return;
        }

        // Check if we should fire today:
        // Did the configured time (config.NoActivity48hTime) fall in (lastTickAt, now]?
        var lastTickBratislava = config.LastTickAt.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(config.LastTickAt.Value, _tz)
            : nowBratislava.AddDays(-1);

        // NoActivity48hTime is stored as TimeSpan (e.g. 18:00). DateOnly.ToDateTime needs TimeOnly.
        var fireTime = todayBratislava.ToDateTime(TimeOnly.FromTimeSpan(config.NoActivity48hTime));

        // If now >= fireTime and lastTickAt < fireTime, we should dispatch
        if (nowBratislava < fireTime || lastTickBratislava >= fireTime)
        {
            _logger.LogDebug("Not yet time to fire notifications (fireTime={fireTime}, now={now}, lastTick={lastTick})",
                fireTime, nowBratislava, lastTickBratislava);
            return;
        }

        _logger.LogInformation("NoActivity48h trigger fires now");

        // Run evaluator
        var evaluator = new NoActivity48hEvaluator(db);
        var candidates = await evaluator.EvaluateAsync(todayBratislava, ct);

        _logger.LogInformation("NoActivity48h found {count} candidates", candidates.Count);

        // Hard cap: 200 sends per tick
        if (candidates.Count > 200)
        {
            _logger.LogWarning("NoActivity48h would send {count} notifications — capped at 200 for safety", candidates.Count);
            candidates = candidates.Take(200).ToList();
        }

        int totalSends = 0;

        foreach (var candidate in candidates)
        {
            // Check idempotency: already sent Push today?
            var alreadySent = await db.NotificationLogs
                .AnyAsync(l =>
                    l.EmployeeId == candidate.Employee.Id &&
                    l.Channel == "Push" &&
                    l.TriggerType == "NoActivity48h" &&
                    l.TriggerDate == todayBratislava,
                ct);

            if (alreadySent)
            {
                _logger.LogDebug("Skipping {name} — already sent today", candidate.Employee.FirstName);
                continue;
            }

            // Send Push to all subscriptions
            var subscriptions = await db.PushSubscriptions
                .Where(s => s.EmployeeId == candidate.Employee.Id)
                .ToListAsync(ct);

            foreach (var sub in subscriptions)
            {
                var result = await pushService.SendAsync(
                    sub,
                    candidate.PushTitle,
                    candidate.PushBody,
                    "/kiosk",
                    ct);

                var logEntry = new NotificationLog
                {
                    EmployeeId = candidate.Employee.Id,
                    Channel = "Push",
                    TriggerType = "NoActivity48h",
                    Body = candidate.PushBody,
                    TriggerDate = todayBratislava,
                    SentAt = DateTime.UtcNow,
                    Status = result.Success ? "Sent" : (result.IsGoneStale ? "Skipped" : "Failed"),
                    ProviderMessageId = result.ProviderMessageId,
                    ErrorMessage = result.ErrorMessage
                };

                db.NotificationLogs.Add(logEntry);
                totalSends++;

                // If stale, delete the subscription
                if (result.IsGoneStale)
                {
                    _logger.LogInformation("Removing stale subscription {endpoint}", sub.Endpoint);
                    db.PushSubscriptions.Remove(sub);
                }
            }

            if (subscriptions.Count == 0)
            {
                // Log that no subscription exists
                var logEntry = new NotificationLog
                {
                    EmployeeId = candidate.Employee.Id,
                    Channel = "Push",
                    TriggerType = "NoActivity48h",
                    Body = candidate.PushBody,
                    TriggerDate = todayBratislava,
                    SentAt = DateTime.UtcNow,
                    Status = "NoSubscription",
                    ErrorMessage = "No push subscriptions found"
                };
                db.NotificationLogs.Add(logEntry);
            }
        }

        // Update LastTickAt
        config.LastTickAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("NoActivity48h dispatch complete: {totalSends} notifications sent", totalSends);
    }
}
