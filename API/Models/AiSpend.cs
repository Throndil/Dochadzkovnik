namespace API.Models;

/// <summary>
/// Running monthly total of paid AI extraction (Claude Sonnet). Accumulated
/// from the exact `usage` block every Messages API response returns, so the
/// number on the Súhrn matches the console to the cent — no admin API key
/// needed. One row per calendar month.
/// </summary>
public class AiSpend
{
    public int Id { get; set; }

    /// <summary>Calendar month "yyyy-MM" (Bratislava local).</summary>
    public string Month { get; set; } = string.Empty;

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Calls { get; set; }

    /// <summary>Accumulated cost in EUR (≈ USD; Sonnet $3/M in, $15/M out).</summary>
    public decimal CostEur { get; set; }

    public DateTime UpdatedAt { get; set; }
}
