using API.Data;
using API.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services;

public class NoActivity48hCandidate
{
    public Employee Employee { get; set; } = null!;
    public string PushTitle { get; set; } = string.Empty;
    public string PushBody { get; set; } = string.Empty;
}

public class NoActivity48hEvaluator
{
    private readonly AppDbContext _db;
    private static readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    public NoActivity48hEvaluator(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Pure logic: evaluates which employees have no activity in the past 48 hours (on working days).
    /// Does NOT check idempotency (NotificationLog history) — that's the caller's responsibility.
    /// </summary>
    public async Task<List<NoActivity48hCandidate>> EvaluateAsync(DateOnly localDate, CancellationToken ct)
    {
        var candidates = new List<NoActivity48hCandidate>();

        // Convert local date to UTC for TimeEntry queries
        var tzDate = TimeZoneInfo.ConvertTimeToUtc(localDate.ToDateTime(TimeOnly.MinValue), _tz);
        var twoDaysAgo = tzDate.AddDays(-2);

        var employees = await _db.Employees
            .Where(e => e.IsActive && e.NotificationsEnabled)
            .ToListAsync(ct);

        foreach (var emp in employees)
        {
            // Skip if employee created less than 3 days ago
            if (DateTime.UtcNow.Subtract(emp.CreatedAt).TotalDays < 3)
                continue;

            // Check if any TimeEntry with ClockIn >= twoDaysAgo
            var hasRecentActivity = await _db.TimeEntries
                .AnyAsync(t => t.EmployeeId == emp.Id && t.ClockIn >= twoDaysAgo, ct);

            if (!hasRecentActivity)
            {
                candidates.Add(new NoActivity48hCandidate
                {
                    Employee = emp,
                    PushTitle = "Nezabudni na hodiny",
                    PushBody = "Posledné 2 dni nemáš zapísané hodiny. Otvor aplikáciu a doplň ich."
                });
            }
        }

        return candidates;
    }
}
