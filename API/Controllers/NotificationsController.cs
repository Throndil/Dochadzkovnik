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

        int successCount = 0;
        foreach (var sub in subscriptions)
        {
            var result = await _pushService.SendAsync(sub, title, body, "/kiosk");

            var logEntry = new NotificationLog
            {
                EmployeeId = emp.Id,
                Channel = "Push",
                TriggerType = "Test",
                Body = body,
                TriggerDate = DateOnly.FromDateTime(DateTime.UtcNow),
                SentAt = DateTime.UtcNow,
                Status = result.Success ? "Sent" : "Failed",
                ErrorMessage = result.ErrorMessage
            };
            _db.NotificationLogs.Add(logEntry);

            if (result.Success)
                successCount++;

            if (result.IsGoneStale)
                _db.PushSubscriptions.Remove(sub);
        }

        await _db.SaveChangesAsync();

        return Ok(new NotificationTestResultDto
        {
            Success = successCount > 0,
            Message = $"Odoslané {successCount} z {subscriptions.Count} push upozornení",
            SendCount = successCount
        });
    }

    // Admin: fire NoActivity48h check right now
    [Authorize]
    [HttpPost("fire-now")]
    public async Task<ActionResult<NotificationTestResultDto>> FireNow()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");
        var nowBratislava = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var todayBratislava = DateOnly.FromDateTime(nowBratislava);

        var evaluator = new NoActivity48hEvaluator(_db);
        var candidates = await evaluator.EvaluateAsync(todayBratislava, CancellationToken.None);

        int totalSends = 0;

        foreach (var candidate in candidates)
        {
            var alreadySent = await _db.NotificationLogs
                .AnyAsync(l =>
                    l.EmployeeId == candidate.Employee.Id &&
                    l.Channel == "Push" &&
                    l.TriggerType == "NoActivity48h" &&
                    l.TriggerDate == todayBratislava);

            if (alreadySent)
                continue;

            var subscriptions = await _db.PushSubscriptions
                .Where(s => s.EmployeeId == candidate.Employee.Id)
                .ToListAsync();

            foreach (var sub in subscriptions)
            {
                var result = await _pushService.SendAsync(sub, candidate.PushTitle, candidate.PushBody, "/kiosk");

                var logEntry = new NotificationLog
                {
                    EmployeeId = candidate.Employee.Id,
                    Channel = "Push",
                    TriggerType = "NoActivity48h",
                    Body = candidate.PushBody,
                    TriggerDate = todayBratislava,
                    SentAt = DateTime.UtcNow,
                    Status = result.Success ? "Sent" : "Failed",
                    ErrorMessage = result.ErrorMessage
                };
                _db.NotificationLogs.Add(logEntry);
                totalSends++;

                if (result.IsGoneStale)
                    _db.PushSubscriptions.Remove(sub);
            }
        }

        await _db.SaveChangesAsync();

        return Ok(new NotificationTestResultDto
        {
            Success = true,
            Message = $"Odoslané {totalSends} push upozornení",
            SendCount = totalSends
        });
    }

    // Admin: send reminder to specific employee (for demo)
    [Authorize]
    [HttpPost("fire-for-employee")]
    public async Task<ActionResult<NotificationTestResultDto>> FireForEmployee([FromBody] FireForEmployeeDto dto)
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

        int successCount = 0;
        foreach (var sub in subscriptions)
        {
            var result = await _pushService.SendAsync(sub, pushTitle, pushBody, "/kiosk");

            var logEntry = new NotificationLog
            {
                EmployeeId = emp.Id,
                Channel = "Push",
                TriggerType = "NoActivity48h",
                Body = pushBody,
                TriggerDate = todayBratislava,
                SentAt = DateTime.UtcNow,
                Status = result.Success ? "Sent" : "Failed",
                ErrorMessage = result.ErrorMessage
            };
            _db.NotificationLogs.Add(logEntry);

            if (result.Success)
                successCount++;

            if (result.IsGoneStale)
                _db.PushSubscriptions.Remove(sub);
        }

        await _db.SaveChangesAsync();

        return Ok(new NotificationTestResultDto
        {
            Success = successCount > 0,
            Message = $"Odoslané {successCount} z {subscriptions.Count} upozornení",
            SendCount = successCount
        });
    }

    // Admin: reset today's notifications (dev/demo only)
    [Authorize]
    [HttpPost("reset-today")]
    public async Task<ActionResult<dynamic>> ResetToday()
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

    private async Task<Employee?> FindEmployeeByPin(string pin)
    {
        var employees = await _db.Employees
            .Where(e => e.IsActive)
            .ToListAsync();

        return employees.FirstOrDefault(e => _pinHasher.Verify(pin, e.Pin));
    }
}
