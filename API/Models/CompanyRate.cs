namespace API.Models;

/// <summary>
/// One configurable company amount on the "Odvody" page — what the firm pays
/// on top of wages or per event: odvody, ubytovanie (1 €), výjazd auta (30 €)…
/// The customer edits amounts (and adds his own rows) without a developer;
/// W1 (hrubá sadzba) and F5 (výjazdy) read from here. Seeded in
/// AppDbContext.OnModelCreating.
/// </summary>
public class CompanyRate
{
    public int Id { get; set; }

    /// <summary>Stable machine key for rows code reads ("ubytovanie",
    /// "vyjazd_auta", "odvody"); null on customer-added rows.</summary>
    public string? Key { get; set; }

    /// <summary>Display name, e.g. "Ubytovanie".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>EUR amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>Human unit hint, e.g. "€/h na pracovníka", "€/výjazd".</summary>
    public string? Unit { get; set; }

    public DateTime UpdatedAt { get; set; }
}
