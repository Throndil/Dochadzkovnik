namespace API.Services;

/// <summary>
/// Shared health/quota state for the invoice-scanning pipeline, surfaced to
/// the client via GET /api/invoices/scan-status. The point is to warn the
/// customer BEFORE they photograph ten pages for nothing: AI daily quota
/// exhausted (pipeline runs in the Document AI fallback mode until the
/// reset), OCR outage (AI carries alone), or a full outage.
///
/// Singleton — written by the extractor/controller, read by the status
/// endpoint. All timestamps UTC.
/// </summary>
public sealed class ScanStatusService
{
    private readonly object _lock = new();

    public DateTime? AiExhaustedUntilUtc { get; private set; }
    public DateTime? LastAiFailureUtc { get; private set; }
    public DateTime? LastOcrFailureUtc { get; private set; }

    /// <summary>Daily free-tier quota hit — AI is out until the reset.</summary>
    public void MarkAiQuotaExhausted()
    {
        lock (_lock) AiExhaustedUntilUtc = NextGeminiResetUtc();
    }

    public void MarkAiTransientFailure()
    {
        lock (_lock) LastAiFailureUtc = DateTime.UtcNow;
    }

    public void MarkAiOk()
    {
        lock (_lock)
        {
            AiExhaustedUntilUtc = null;
            LastAiFailureUtc = null;
        }
    }

    public void MarkOcrFailure()
    {
        lock (_lock) LastOcrFailureUtc = DateTime.UtcNow;
    }

    public void MarkOcrOk()
    {
        lock (_lock) LastOcrFailureUtc = null;
    }

    public bool AiExhausted => AiExhaustedUntilUtc is { } t && t > DateTime.UtcNow;

    /// <summary>A failure keeps the component "unhealthy" for 10 minutes —
    /// long enough for the banner to matter, short enough to self-heal.</summary>
    public bool AiUnhealthy => LastAiFailureUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromMinutes(10);

    public bool OcrUnhealthy => LastOcrFailureUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromMinutes(10);

    /// <summary>Gemini free-tier daily quotas reset at midnight Pacific.</summary>
    private static DateTime NextGeminiResetUtc()
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");   // Linux (Railway)
        }
        catch
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");   // Windows dev
            }
            catch
            {
                return DateTime.UtcNow.Date.AddDays(1).AddHours(8);   // ≈ midnight PT
            }
        }
        var nowPt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var nextMidnightPt = DateTime.SpecifyKind(nowPt.Date.AddDays(1), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(nextMidnightPt, tz);
    }
}
