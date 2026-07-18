namespace API.Models;

/// <summary>
/// One item on a purchase receipt — e.g. "5 vriec Cement at 4,80 €".
/// Lines start with <see cref="MaterialId"/> = null when the worker free-typed
/// a name on the kiosk; the admin promotes them to the catalogue afterwards
/// (see MATERIAL_PURCHASES_PLAN.md "Promotion flow"). The <see cref="MaterialNameRaw"/>
/// is preserved even after promotion so the audit trail survives renames.
/// </summary>
public class MaterialPurchaseLine
{
    public int Id { get; set; }

    public int PurchaseId { get; set; }

    /// <summary>
    /// Catalogue link, populated only after the admin promotes / merges this line.
    /// Null while the line is "neidentifikovaný".
    /// </summary>
    public int? MaterialId { get; set; }

    /// <summary>
    /// Raw name as typed at the kiosk (or chosen from the catalogue at insert time).
    /// Always stored — survives renames + admin merges so we can prove what the
    /// worker actually entered.
    /// </summary>
    public string MaterialNameRaw { get; set; } = string.Empty;

    /// <summary>Unit snapshotted from the catalogue or typed by the worker.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Amount purchased on this line, in the line's <see cref="Unit"/>.</summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Price actually paid this trip, EUR per <see cref="Unit"/>. Independent of
    /// <see cref="Material.PricePerUnit"/> — this is the purchase-side snapshot
    /// (consumption-side snapshot lives on <see cref="MaterialUsage.UnitPriceAtTime"/>).
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Denormalised = Quantity * UnitPrice. Recomputed on every save.</summary>
    public decimal LineTotal { get; set; }

    // ─── Invoice scanning extensions (INVOICE_SCANNING_PLAN.md) ──────
    // The kiosk-entered lines leave most of these null. Scanned-invoice
    // lines populate every field that appeared on the source document.

    /// <summary>Supplier's catalogue / SKU code, e.g. "1681011188".</summary>
    public string? SupplierItemCode { get; set; }

    /// <summary>"cena MJ cenník bez DPH" — list price excl. VAT, before discount.</summary>
    public decimal? ListPriceExclVat { get; set; }

    /// <summary>"zľava" — discount % (e.g. 38.00 for 38%).</summary>
    public decimal? DiscountPercent { get; set; }

    /// <summary>
    /// "cena MJ po zľave s DPH" — discounted unit price incl. VAT.
    /// Denormalised display value; the canonical price is <see cref="UnitPrice"/>
    /// (which is excl. VAT, post-discount, matching the existing schema).
    /// </summary>
    public decimal? UnitPriceInclVat { get; set; }

    /// <summary>
    /// VAT rate on this line. 23.00 = standard SK rate; 0.00 = reverse-charge
    /// or exempt. Defaults to 23.00 for back-compat with pre-scanning lines.
    /// </summary>
    public decimal VatRate { get; set; } = 23m;

    /// <summary>
    /// True for "** Prenesenie daňovej povinnosti" (reverse-charge) lines.
    /// Tax-significant — the customer files this differently. See the DEK
    /// invoice's KR KH 20 lines for examples.
    /// </summary>
    public bool IsReverseCharge { get; set; } = false;

    /// <summary>
    /// True for service / rental lines (e.g. "Prenájom - Vibračná doska").
    /// They are still MaterialPurchaseLines but don't represent physical
    /// inventory the way ordinary material lines do.
    /// </summary>
    public bool IsService { get; set; } = false;

    /// <summary>
    /// Optional per-line site override. Null = inherit the parent delivery
    /// list's <see cref="MaterialPurchase.LocationId"/> (the default; how every
    /// line behaved before this field existed). Set to a specific Location when
    /// one receipt / delivery list is split across sites — e.g. the worker buys
    /// three things and drops two of them at two different pracoviská on the way.
    /// Effective site of a line is therefore <c>LocationId ?? Purchase.LocationId</c>.
    /// </summary>
    public int? LocationId { get; set; }

    /// <summary>
    /// JSON array of edit records for this line — the audit trail. Each entry
    /// is { field, oldValue, newValue, editedBy, editedAt }. Appended by the
    /// invoice review PUT endpoints. Null on kiosk-entered lines that have
    /// never been edited.
    /// </summary>
    public string? LineEditHistory { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Per-line Mašina/Auto override (stroje docs — e.g. one Slovnaft
    /// fuel invoice, each line a different vehicle). Effective asset =
    /// line ?? delivery list ?? document tag.</summary>
    public int? MachineId { get; set; }
    public int? CarId { get; set; }

    public MaterialPurchase Purchase { get; set; } = null!;
    public Material? Material { get; set; }
    public Location? Location { get; set; }
    public Machine? Machine { get; set; }
    public Car? Car { get; set; }
}
