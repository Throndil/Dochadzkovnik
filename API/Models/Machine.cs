namespace API.Models;

/// <summary>
/// One machine of the AZ Stroje division — bager, nakladač, vozidlo na presun
/// materiálu… (Fáza F0). Mirrors <see cref="Car"/>: registry + optional photo;
/// the kiosk transport choice (Auto / Stroj / Pešo, F3) links TimeEntries
/// here, and division costs can carry an optional informational machine tag
/// (F1). Division money itself is computed on the DIVISION (D4), never on
/// the machine.
/// </summary>
public class Machine
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Free note — model, evidence number, operator…</summary>
    public string? Note { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TimeEntry> TimeEntries { get; set; } = [];
}
