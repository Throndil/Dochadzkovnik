namespace API.Models;

/// <summary>
/// One purchase event — the header for a single shopping trip / receipt.
/// May contain many <see cref="MaterialPurchaseLine"/> rows.
/// Created either via the kiosk Nákup materiálu flow (PIN-validated) or
/// the admin Materiál → Nákupy page. See MATERIAL_PURCHASES_PLAN.md for design.
/// </summary>
public class MaterialPurchase
{
    public int Id { get; set; }

    /// <summary>When the purchase happened. Stored as Europe/Bratislava local time at insert.</summary>
    public DateTime PurchaseDate { get; set; }

    /// <summary>Who bought it. Required for accountability — never null.</summary>
    public int EmployeeId { get; set; }

    /// <summary>
    /// Site the materials are FOR. Null means "general / company stock"
    /// (no specific site). Where the worker BOUGHT them is irrelevant; this
    /// is the destination, not the origin.
    /// </summary>
    public int? LocationId { get; set; }

    /// <summary>
    /// Optional link back to the kiosk šichta TimeEntry that produced this
    /// purchase. Populated by the in-šichta combined kiosk flow when the
    /// worker selected the configured trigger Location (default name
    /// "Nákup materiálu"). Null when the standalone Nákup tile was used.
    /// </summary>
    public int? TimeEntryId { get; set; }

    /// <summary>Free-text supplier name. V1 — no Supplier table yet.</summary>
    public string? SupplierName { get; set; }

    /// <summary>Cloudinary URL of the receipt scan, if uploaded. One photo per receipt.</summary>
    public string? ReceiptPhotoUrl { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// Denormalised sum of <see cref="MaterialPurchaseLine.LineTotal"/>.
    /// Recomputed by the controller every time lines are added/edited/removed
    /// so reports do not need to JOIN to lines for the headline number.
    /// </summary>
    public decimal TotalCost { get; set; } = 0m;

    // ─── Invoice scanning extensions (INVOICE_SCANNING_PLAN.md) ──────
    // Populated when this purchase originated from a scanned supplier
    // invoice; null on kiosk- or admin-entered purchases.

    /// <summary>Parent invoice document, if this purchase was produced by an invoice scan.</summary>
    public int? InvoiceDocumentId { get; set; }

    /// <summary>Supplier's delivery note reference, e.g. "DL-100-26-015474".</summary>
    public string? DeliveryNoteRef { get; set; }

    /// <summary>"prevzal:" — who picked up the goods (free-text from the invoice).</summary>
    public string? PickedUpBy { get; set; }

    /// <summary>"Pozn.DL:" — free-text delivery-note remark from the invoice.</summary>
    public string? DeliveryNote { get; set; }

    /// <summary>
    /// Original "akcia:" text from the invoice header for this delivery list,
    /// preserved so the GET endpoint can retry auto-matching to a Location
    /// after the manager creates a new Pracovisko. Null when the invoice
    /// header had no akcia or was just "." (placeholder).
    /// </summary>
    public string? AkciaName { get; set; }

    /// <summary>
    /// This delivery list's <c>cena spolu bez DPH</c>. Denormalised so the
    /// review screen can show subtotal without joining to lines.
    /// </summary>
    public decimal? SubtotalExclVat { get; set; }

    /// <summary>
    /// This delivery list's DPH amount across all VAT rates. Server computes
    /// it from the lines at save / reconciliation time.
    /// </summary>
    public decimal? SubtotalVat { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Mašina/Auto assignment of this delivery list (Fáza F1 —
    /// stroje-division docs map to assets instead of pracoviská). Feeds the
    /// per-mašina spending report; division money still computes on the
    /// division (D4).</summary>
    public int? MachineId { get; set; }
    public int? CarId { get; set; }

    public Employee Employee { get; set; } = null!;
    public Location? Location { get; set; }
    public Machine? Machine { get; set; }
    public Car? Car { get; set; }
    public TimeEntry? TimeEntry { get; set; }
    public InvoiceDocument? InvoiceDocument { get; set; }
    public ICollection<MaterialPurchaseLine> Lines { get; set; } = [];
}
