namespace API.Models;

/// <summary>
/// Catalogue of material types the firm uses on construction sites
/// (e.g. Cement / vrece, Voda / l, Obklad / m², Dlažba / m²).
/// Editable by admin users via /admin/materials.
/// </summary>
public class Material
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;   // Slovak name, e.g. "Cement"
    public string Unit { get; set; } = string.Empty;   // Free-text unit, e.g. "vrece", "l", "m²", "kg", "ks"

    /// <summary>
    /// Current catalogue price per <see cref="Unit"/> in EUR. Customer can change this at any time
    /// to adapt to inflation; existing <see cref="MaterialUsage"/> rows are unaffected because they
    /// snapshot <see cref="MaterialUsage.UnitPriceAtTime"/> at insert time.
    /// </summary>
    public decimal PricePerUnit { get; set; } = 0m;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MaterialUsage> Usages { get; set; } = [];
}
