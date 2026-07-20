namespace API.Provisioning;

/// <summary>
/// Canonical subscription bundle → feature-flag map. Single source of truth for
/// which modules each paid tier unlocks. Consumed by the flag seed in Program.cs
/// when a fresh install is provisioned with Provision:Tier (e.g. "Profi").
///
/// Tiers are cumulative: Profi = Start + its own flags, Komplet = Profi + its own.
/// Flag keys must match the rows seeded in Program.cs (knownFlags) and the
/// [RequireFeatureOrSuperAdmin("...")] attributes on the controllers.
/// </summary>
public static class SubscriptionTiers
{
    /// <summary>Štart — attendance core + planner. No money/ops modules.</summary>
    private static readonly string[] Start =
    {
        "Planner",
        "Notifications",
    };

    /// <summary>Profi — adds payroll/P&L, invoice OCR, vehicles, material, work diaries.</summary>
    private static readonly string[] Profi =
    {
        "PayrollAndPnL",
        "InvoiceScanning",
        "InvoiceCameraScan",
        "Vehicles",
        "MaterialPurchases",
        "ProofOfWorkChoices",
    };

    /// <summary>Komplet — adds CNC machines/divisions and Commander integration.</summary>
    private static readonly string[] Komplet =
    {
        "StrojeDivisions",
        "CommanderIntegration",
    };

    /// <summary>Flags enabled for a given tier name (case-insensitive), cumulative. Null if unknown.</summary>
    public static HashSet<string>? EnabledFor(string? tier) => (tier ?? "").Trim().ToLowerInvariant() switch
    {
        "start" or "štart" => new HashSet<string>(Start),
        "profi"            => new HashSet<string>(Start.Concat(Profi)),
        "komplet"          => new HashSet<string>(Start.Concat(Profi).Concat(Komplet)),
        _ => null,
    };
}
