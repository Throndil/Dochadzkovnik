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

    /// <summary>
    /// Origin tag for usages auto-created from a scanned invoice (Option A flow
    /// in INVOICE_SCANNING_PLAN.md). When set, this usage was minted by the
    /// invoice commit step from the referenced <see cref="MaterialPurchaseLine"/>.
    /// Null on usages created manually via the admin UI.
    ///
    /// FK is ON DELETE Cascade: discarding the invoice (which deletes its
    /// purchase lines) sweeps the auto-created usage rows along with it, so
    /// the Pracovisko view stays consistent with what's in the books.
    /// </summary>
    public int? SourceMaterialPurchaseLineId { get; set; }

    /// <summary>
    /// True when this usage came from a <see cref="MaterialPurchaseLine"/> whose
    /// <c>IsService</c> flag is set (typically <c>Prenájom</c> rentals on a
    /// scanned invoice). Surfaces as a "Faktúra (služba)" badge on the
    /// Pracovisko <c>Spotreba materiálu</c> view. Always <c>false</c> on
    /// manually-entered usages and on usages minted from material lines.
    /// </summary>
    public bool IsService { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Location Location { get; set; } = null!;
    public Material Material { get; set; } = null!;
    public Employee? Employee { get; set; }
    public MaterialPurchaseLine? SourceMaterialPurchaseLine { get; set; }
}
