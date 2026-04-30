namespace API.Models;

/// <summary>
/// Persistent runtime toggle for features that should be hidden from the customer
/// until they're signed off. Read by FeatureFlagsController and the Angular
/// FeatureFlagService; written only by the superadmin via the Funkcie card on
/// the Account page. One row per feature, identified by Key.
/// Seeded keys: "Notifications" (default false in prod).
/// </summary>
public class FeatureFlag
{
    /// <summary>Stable identifier (e.g., "Notifications"). Primary key.</summary>
    public string Key { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
