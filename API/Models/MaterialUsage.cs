namespace API.Models;

/// <summary>
/// A single material consumption record on a construction site.
/// E.g. "Stavba Bratislava-Juh used 5 vriec cementu on 2026-04-22".
/// </summary>
public class MaterialUsage
{
    public int Id { get; set; }
    public int LocationId { get; set; }
    public int MaterialId { get; set; }
    public int? EmployeeId { get; set; }            // optional: who logged it (manager who recorded the usage)
    public decimal Quantity { get; set; }            // amount used in the material's Unit

    /// <summary>
    /// Snapshot of <see cref="Material.PricePerUnit"/> at the moment this usage was recorded.
    /// Inflation-protected: changing the catalogue price later does NOT alter past records.
    /// Stored as decimal(12,4); line cost = Quantity * UnitPriceAtTime.
    /// </summary>
    public decimal UnitPriceAtTime { get; set; } = 0m;

    public DateTime Date { get; set; }               // when the material was used (date only, stored as midnight UTC)
    public string? Note { get; set; }
    public string? PhotoUrl { get; set; }            // optional delivery slip / package photo
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Location Location { get; set; } = null!;
    public Material Material { get; set; } = null!;
    public Employee? Employee { get; set; }
}
