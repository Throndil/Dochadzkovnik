using API.Data;
using API.DTOs;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPinHasher _pinHasher;
    private readonly IPushNotificationService _pushService;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        AppDbContext db,
        IPinHasher pinHasher,
        IPushNotificationService pushService,
        ILogger<NotificationsController> logger)
    {
        _db = db;
        _pinHasher = pinHasher;
        _pushService = pushService;
        _logger = logger;
    }

    // Anonymous endpoint: get VAPID public key for service worker
    [HttpGet("vapid-public-key")]
    [AllowAnonymous]
    public async Task<ActionResult<VapidPublicKeyDto>> GetVapidPublicKey()
    {
        var config = await _db.NotificationConfigs
            .FirstOrDefaultAsync(c => c.Id == 1);

        if (config == null || string.IsNullOrEmpty(config.VapidPublicKey))
            return BadRequest("VAPID public key not configured");

        return Ok(new VapidPublicKeyDto { PublicKey = config.VapidPublicKey });
    }

    // Kiosk: subscribe worker to push notifications
    [HttpPost("subscribe")]
    [AllowAnonymous]
    public async Task<ActionResult<dynamic>> Subscribe([FromBody] PushSubscribeDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var employee = await FindEmployeeByPin(dto.Pin);
            if (employee == null)
                return Unauthorized("Neplatný PIN");

            // Upsert by Endpoint: if endpoint exists, update it; otherwise create it
            var existing = await _db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.Endpoint == dto.Subscription.Endpoint);

            if (existing != null)
            {
                existing.EmployeeId = employee.Id;
                existing.P256dhKey = dto.Subscription.Keys.P256dh;
                existing.AuthKey = dto.Subscription.Keys.Auth;
                existing.UserAgent = dto.Subscription.UserAgent;
                existing.LastUsedAt = DateTime.UtcNow;
                _db.PushSubscriptions.Update(existing);
            }
            else
            {
                var subscription = new PushSubscription
                {
                    EmployeeId = employee.Id,
                    Endpoint = dto.Subscription.Endpoint,
                    P256dhKey = dto.Subscription.Keys.P256dh,
                    AuthKey = dto.Subscription.Keys.Auth,
                    UserAgent = dto.Subscription.UserAgent,
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow
                };
                _db.PushSubscriptions.Add(subscription);
            }

            await _db.SaveChangesAsync();
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscribe endpoint error for employeeId hint {EmployeeId}", dto.EmployeeId);
            return StatusCode(500, new { error = "Interná chyba servera. Skúste znova neskôr.", detail = ex.Message });
        }
    }

    // Kiosk: unsubscribe from push notifications
    [HttpDelete("subscribe")]
    [AllowAnonymous]
    public async Task<ActionResult<dynamic>> Unsubscribe([FromBody] PushUnsubscribeDto dto)
    {
        var subscription = await _db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == dto.Endpoint);

        if (subscription != null)
        {
            _db.PushSubscriptions.Remove(subscription);
            await _db.SaveChangesAsync();
        }

        return Ok(new { ok = true });
    }

    // Admin: get notification config
    [Authorize]
    [HttpGet("config")]
    public async Task<ActionResult<NotificationConfigDto>> GetConfig()
    {
        var config = await _db.NotificationConfigs
            .FirstOrDefaultAsync(c => c.Id == 1);

        if (config == null)
            return NotFound();

        var timeStr = $"{config.NoActivity48hTime.Hours:D2}:{config.NoActivity48hTime.Minutes:D2}";

        return Ok(new NotificationConfigDto
        {
            NoActivity48hEnabled = config.NoActivity48hEnabled,
            NoActivity48hTime = timeStr,
            WorkingDaysOnly = config.WorkingDaysOnly,
            ManagerSummaryEnabled = config.ManagerSummaryEnabled,
            ManagerSummaryEmployeeId = config.ManagerSummaryEmployeeId
        });
    }

    // Admin: update notification config
    [Authorize]
    [HttpPut("config")]
    public async Task<ActionResult<NotificationConfigDto>> UpdateConfig([FromBody] UpdateNotificationConfigDto dto)
    {
        var config = await _db.NotificationConfigs
            .FirstOrDefaultAsync(c => c.Id == 1);

        if (config == null)
            return NotFound();

        if (dto.NoActivity48hEnabled.HasValue)
            config.NoActivity48hEnabled = dto.NoActivity48hEnabled.Value;

        if (!string.IsNullOrEmpty(dto.NoActivity48hTime))
        {
            if (TimeSpan.TryParse(dto.NoActivity48hTime, out var ts))
                config.NoActivity48hTime = ts;
        }

        if (dto.WorkingDaysOnly.HasValue)
            config.WorkingDaysOnly = dto.WorkingDaysOnly.Value;

        if (dto.ManagerSummaryEnabled.HasValue)
            config.ManagerSummaryEnabled = dto.ManagerSummaryEnabled.Value;

        config.ManagerSummaryEmployeeId = dto.ManagerSummaryEmployeeId;

        await _db.SaveChangesAsync();

        var timeStr = $"{config.NoActivity48hTime.Hours:D2}:{config.NoActivity48hTime.Minutes:D2}";
        return Ok(new NotificationConfigDto
        {
            NoActivity48hEnabled = config.NoActivity48hEnabled,
            NoActivity48hTime = timeStr,
            WorkingDaysOnly = config.WorkingDaysOnly,
            ManagerSummaryEnabled = config.ManagerSummaryEnabled,
            ManagerSummaryEmployeeId = config.ManagerSummaryEmployeeId
        });
    }

    // Admin: get notification history
    [Authorize]
    [HttpGet("history")]
    public async Task<ActionResult<List<NotificationLogEntryDto>>> GetHistory(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? employeeId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var query = _db.NotificationLogs.AsQueryable();

        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var fromDate))
            query = query.Where(l => l.TriggerDate >= fromDate);

        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var toDate))
            query = query.Where(l => l.TriggerDate <= toDate);

        if (employeeId.HasValue)
            query = query.Where(l => l.EmployeeId == employeeId);

        var logs = await query
            .OrderByDescending(l => l.SentAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // Fetch employee names for those logs with EmployeeId
        var employeeIds = logs.Where(l => l.EmployeeId.HasValue).Select(l => l.EmployeeId!.Value).Distinct().ToList();
        var employees = await _db.Employees
            .Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);

        return Ok(logs.Select(l => new NotificationLogEntryDto
        {
            Id = l.Id,
            EmployeeId = l.EmployeeId,
            EmployeeName = l.EmployeeId.HasValue && employees.TryGetValue(l.EmployeeId.Value, out var emp)
                ? $"{emp.FirstName} {emp.LastName}"
                : "(Unknown)",
            Channel = l.Channel,
            TriggerType = l.TriggerType,
            Body = l.Body,
            TriggerDate = l.TriggerDate.ToString("yyyy-MM-dd"),
            SentAt = l.SentAt,
            Status = l.Status,
            ErrorMessage = l.ErrorMessage
        }).ToList());
    }

    // Admin: get per-employee notification status
    [Authorize]
    [HttpGet("employees")]
    public async Task<ActionResult<List<NotificationEmployeeStatusDto>>> GetEmployeeStatuses()
    {
        var employees = await _db.Employees
            .Where(e => e.IsActive)
            .ToListAsync();

        var result = new List<NotificationEmployeeStatusDto>();

        foreach (var emp in employees)
        {
            var subCount = await _db.PushSubscriptions.CountAsync(s => s.EmployeeId == emp.Id);
            var lastLog = await _db.NotificationLogs
                .Where(l => l.EmployeeId == emp.Id)
                .OrderByDescending(l => l.SentAt)
                .FirstOrDefaultAsync();

            result.Add(new NotificationEmployeeStatusDto
            {
                Id = emp.Id,
                FirstName = emp.FirstName,
                LastName = emp.LastName,
                FullName = $"{emp.FirstName} {emp.LastName}",
                PhoneNumber = emp.PhoneNumber,
                NotificationsEnabled = emp.NotificationsEnabled,
                PushSubscriptionCount = subCount,
                LastNotifiedAt = lastLog?.SentAt
            });
        }

        return Ok(result);
    }

    // Admin: update notification settings for one employee
    [Authorize]
    [HttpPut("employees/{id}")]
    public async Task<ActionResult<NotificationEmployeeStatusDto>> UpdateEmployeeNotifications(
        int id,
        [FromBody] dynamic updates)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null)
            return NotFound();

        // Round-trip the dynamic body through JSON to a strongly-typed dictionary so
        // the rest of this method is statically typed (avoids CS8197 with dynamic).
        Dictionary<string, object>? updateDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            (string)System.Text.Json.JsonSerializer.Serialize(updates));

        if (updateDict != null)
        {
            if (updateDict.TryGetValue("notificationsEnabled", out object? notifVal))
                emp.NotificationsEnabled = Convert.ToBoolean(notifVal?.ToString());
        }

        await _db.SaveChangesAsync();

        var subCount = await _db.PushSubscriptions.CountAsync(s => s.EmployeeId == emp.Id);
        var lastLog = await _db.NotificationLogs
            .Where(l => l.EmployeeId == emp.Id)
            .OrderByDescending(l => l.SentAt)
            .FirstOrDefaultAsync();

        return Ok(new NotificationEmployeeStatusDto
        {
            Id = emp.Id,
            FirstName = emp.FirstName,
            LastName = emp.LastName,
            FullName = $"{emp.FirstName} {emp.LastName}",
            PhoneNumber = emp.PhoneNumber,
            NotificationsEnabled = emp.NotificationsEnabled,
            PushSubscriptionCount = subCount,
            LastNotifiedAt = lastLog?.SentAt
        });
    }

    // Admin: send test push notification
    [Authorize]
    [HttpPost("test/push")]
    public async Task<ActionResult<NotificationTestResultDto>> TestPush([FromBody] TestPushRequestDto dto)
    {
        try
        {
            var emp = await _db.Employees.FindAsync(dto.EmployeeId);
            if (emp == null)
                return NotFound("Zamestnanec nenájdený");

            var subscriptions = await _db.PushSubscriptions
                .Where(s => s.EmployeeId == emp.Id)
                .ToListAsync();

            if (subscriptions.Count == 0)
                return Ok(new NotificationTestResultDto
                {
                    Success = false,
                    ErrorMessage = "Žiadne push notifikácie zaregistrované pre tohto zamestnanca"
                });

            var title = dto.Title ?? "Šichtovnica TEST";
            var body = dto.Body ?? $"Toto je testovacie upozornenie. Ak ho vidíš, push funguje. {DateTime.Now:HH:mm}";
            // Unique per press so the SW doesn't collapse this with any prior notification still
            // in the system tray (W3C tag semantics replace silently when tags match).
            var pushTag = $"sichtovnica-test-{emp.Id}-{DateTime.UtcNow.Ticks}";

            int successCount = 0;
            var errors = new List<string>();
            foreach (var sub in subscriptions)
            {
                var result = await _pushService.SendAsync(sub, title, body, "/kiosk", pushTag);

                if (result.Success)
                    successCount++;
                else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    errors.Add(result.ErrorMessage);

                if (result.IsGoneStale)
                    _db.PushSubscriptions.Remove(sub);
            }

            // Single audit row per logical send (Option A from the unique-index fix).
            // The unique index (EmployeeId, Channel, TriggerType, TriggerDate) forbids
            // more than one row per day per tuple, so we replace any prior row first —
            // the Test button is meant to be pressable repeatedly during a day.
            var triggerDate = DateOnly.FromDateTime(DateTime.UtcNow);
            var priorLogs = await _db.NotificationLogs
                .Where(l => l.EmployeeId == emp.Id
                         && l.Channel == "Push"
                         && l.TriggerType == "Test"
                         && l.TriggerDate == triggerDate)
                .ToListAsync();
            if (priorLogs.Count > 0)
                _db.NotificationLogs.RemoveRange(priorLogs);

            _db.NotificationLogs.Add(new NotificationLog
            {
                EmployeeId = emp.Id,
                Channel = "Push",
                TriggerType = "Test",
                Body = body,
                TriggerDate = triggerDate,
                SentAt = DateTime.UtcNow,
                Status = successCount > 0 ? "Sent" : "Failed",
                ErrorMessage = errors.Count > 0 ? string.Join("; ", errors) : null
            });

            await _db.SaveChangesAsync();

            return Ok(new NotificationTestResultDto
            {
                Success = successCount > 0,
                Message = $"Odoslané {successCount} z {subscriptions.Count} push upozornení",
                SendCount = successCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TestPush error for employeeId {EmployeeId}", dto.EmployeeId);
            return StatusCode(500, new { error = "Interná chyba servera.", detail = ex.Message });
        }
    }

    // Admin: fire NoActivity48h check right now
    // Query params (both default true so the manual admin button is re-pressable for live demos):
    //   ignoreIdempotency: when true, replace prior log rows instead of skipping employees
    //                      who were already sent a notification today.
    //   ignoreGracePeriod: when true, skip the <3-days-since-CreatedAt onboarding guard so
    //                      newly-created dev/test employees qualify as candidates.
    // The production cron uses NotificationBackgroundService which calls the evaluator with
    // both defaults (false, false), so this query-param relaxation is admin-only.
    [Authorize]
    [HttpPost("fire-now")]
    public async Task<ActionResult<NotificationTestResultDto>> FireNow(
        [FromQuery] bool ignoreIdempotency = true,
        [FromQuery] bool ignoreGracePeriod = true)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");
            var nowBratislava = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var todayBratislava = DateOnly.FromDateTime(nowBratislava);

            var evaluator = new NoActivity48hEvaluator(_db);
            var candidates = await evaluator.EvaluateAsync(
                todayBratislava,
                CancellationToken.None,
                ignoreGracePeriod: ignoreGracePeriod);

            int totalSends = 0;

            foreach (var candidate in candidates)
            {
                if (!ignoreIdempotency)
                {
                    var alreadySent = await _db.NotificationLogs
                        .AnyAsync(l =>
                            l.EmployeeId == candidate.Employee.Id &&
                            l.Channel == "Push" &&
                            l.TriggerType == "NoActivity48h" &&
                            l.TriggerDate == todayBratislava);

                    if (alreadySent)
                        continue;
                }

                var subscriptions = await _db.PushSubscriptions
                    .Where(s => s.EmployeeId == candidate.Employee.Id)
                    .ToListAsync();

                if (subscriptions.Count == 0)
                    continue;

                // Unique per press so successive admin invocations show as distinct toasts.
                var pushTag = $"sichtovnica-noactivity-{candidate.Employee.Id}-{DateTime.UtcNow.Ticks}";

                int successCount = 0;
                var errors = new List<string>();
                foreach (var sub in subscriptions)
                {
                    var result = await _pushService.SendAsync(sub, candidate.PushTitle, candidate.PushBody, "/kiosk", pushTag);

                    if (result.Success)
                        successCount++;
                    else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                        errors.Add(result.ErrorMessage);

                    if (result.IsGoneStale)
                        _db.PushSubscriptions.Remove(sub);
                }

                // Single audit row per (employee, channel, triggerType, day) — the unique index
                // requires it. When ignoreIdempotency=true (manual admin re-press) we replace any
                // prior row for this tuple; when false (cron-style), the early `alreadySent` check
                // above already returned `continue` so there is no conflicting row to delete.
                if (ignoreIdempotency)
                {
                    var priorLogs = await _db.NotificationLogs
                        .Where(l => l.EmployeeId == candidate.Employee.Id
                                 && l.Channel == "Push"
                                 && l.TriggerType == "NoActivity48h"
                                 && l.TriggerDate == todayBratislava)
                        .ToListAsync();
                    if (priorLogs.Count > 0)
                        _db.NotificationLogs.RemoveRange(priorLogs);
                }

                _db.NotificationLogs.Add(new NotificationLog
                {
                    EmployeeId = candidate.Employee.Id,
                    Channel = "Push",
                    TriggerType = "NoActivity48h",
                    Body = candidate.PushBody,
                    TriggerDate = todayBratislava,
                    SentAt = DateTime.UtcNow,
                    Status = successCount > 0 ? "Sent" : "Failed",
                    ErrorMessage = errors.Count > 0 ? string.Join("; ", errors) : null
                });
                totalSends += successCount;
            }

            await _db.SaveChangesAsync();

            return Ok(new NotificationTestResultDto
            {
                Success = true,
                Message = $"Odoslané {totalSends} push upozornení",
                SendCount = totalSends
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FireNow error");
            return StatusCode(500, new { error = "Interná chyba servera.", detail = ex.Message });
        }
    }

    // Admin: send reminder to specific employee (for demo)
    [Authorize]
    [HttpPost("fire-for-employee")]
    public async Task<ActionResult<NotificationTestResultDto>> FireForEmployee([FromBody] FireForEmployeeDto dto)
    {
        try
        {
        var emp = await _db.Employees.FindAsync(dto.EmployeeId);
        if (emp == null)
            return NotFound("Zamestnanec nenájdený");

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");
        var nowBratislava = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var todayBratislava = DateOnly.FromDateTime(nowBratislava);

        var pushTitle = "Nezabudni na hodiny";
        var pushBody = $"Posledné 2 dni nemáš zapísané hodiny v aplikácii Šichtovnica. Otvor aplikáciu a doplň ich, prosím.";
        if (dto.IgnoreIdempotency)
            pushBody += " (demo)";

        // Check idempotency unless overridden
        if (!dto.IgnoreIdempotency)
        {
            var alreadySent = await _db.NotificationLogs
                .AnyAsync(l =>
                    l.EmployeeId == emp.Id &&
                    l.Channel == "Push" &&
                    l.TriggerType == "NoActivity48h" &&
                    l.TriggerDate == todayBratislava);

            if (alreadySent)
                return BadRequest("Upozornenie už bolo odoslané dnes. Použite ignoreIdempotency=true na opakovaní.");
        }

        var subscriptions = await _db.PushSubscriptions
            .Where(s => s.EmployeeId == emp.Id)
            .ToListAsync();

        if (subscriptions.Count == 0)
            return BadRequest("Žiadne push notifikácie zaregistrované");

        // Unique per press so successive demo invocations show as distinct toasts.
        var pushTag = $"sichtovnica-demo-{emp.Id}-{DateTime.UtcNow.Ticks}";

        int successCount = 0;
        var errors = new List<string>();
        foreach (var sub in subscriptions)
        {
            var result = await _pushService.SendAsync(sub, pushTitle, pushBody, "/kiosk", pushTag);

            if (result.Success)
                successCount++;
            else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                errors.Add(result.ErrorMessage);

            if (result.IsGoneStale)
                _db.PushSubscriptions.Remove(sub);
        }

        // Single audit row per logical send (Option A from the unique-index fix).
        // When IgnoreIdempotency=true (demo button) we replace any prior row for the
        // same (employee, channel, triggerType, day) tuple so the unique index does
        // not reject the insert. When false, the early `alreadySent` check above has
        // already returned BadRequest, so there is no conflicting row to delete.
        if (dto.IgnoreIdempotency)
        {
            var priorLogs = await _db.NotificationLogs
                .Where(l => l.EmployeeId == emp.Id
                         && l.Channel == "Push"
                         && l.TriggerType == "NoActivity48h"
                         && l.TriggerDate == todayBratislava)
                .ToListAsync();
            if (priorLogs.Count > 0)
                _db.NotificationLogs.RemoveRange(priorLogs);
        }

        _db.NotificationLogs.Add(new NotificationLog
        {
            EmployeeId = emp.Id,
            Channel = "Push",
            TriggerType = "NoActivity48h",
            Body = pushBody,
            TriggerDate = todayBratislava,
            SentAt = DateTime.UtcNow,
            Status = successCount > 0 ? "Sent" : "Failed",
            ErrorMessage = errors.Count > 0 ? string.Join("; ", errors) : null
        });

        await _db.SaveChangesAsync();

        return Ok(new NotificationTestResultDto
        {
            Success = successCount > 0,
            Message = $"Odoslané {successCount} z {subscriptions.Count} upozornení",
            SendCount = successCount
        });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FireForEmployee error for employeeId {EmployeeId}", dto.EmployeeId);
            return StatusCode(500, new { error = "Interná chyba servera.", detail = ex.Message });
        }
    }

    // Admin: reset today's notifications (dev/demo only)
    [Authorize]
    [HttpPost("reset-today")]
    public async Task<ActionResult<dynamic>> ResetToday()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");
            var nowBratislava = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var todayBratislava = DateOnly.FromDateTime(nowBratislava);

            var logsToDelete = await _db.NotificationLogs
                .Where(l => l.TriggerDate == todayBratislava)
                .ToListAsync();

            _db.NotificationLogs.RemoveRange(logsToDelete);
            await _db.SaveChangesAsync();

            return Ok(new { deletedCount = logsToDelete.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetToday error");
            return StatusCode(500, new { error = "Interná chyba servera.", detail = ex.Message });
        }
    }

    private async Task<Employee?> FindEmployeeByPin(string pin)
    {
        var employees = await _db.Employees
            .Where(e => e.IsActive)
            .ToListAsync();

        return employees.FirstOrDefault(e => _pinHasher.Verify(e.Pin, pin));
    }
}
