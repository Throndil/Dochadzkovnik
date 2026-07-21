namespace API.Models;

/// <summary>
/// One uploaded supplier invoice (PDF or image) parsed by the Google Document
/// AI Invoice Parser. Holds the original document URL, the raw OCR JSON, the
/// extracted header data, and reconciliation state. Parents N
/// <see cref="MaterialPurchase"/> records — one per <c>dodací list</c>
/// section in the invoice. See INVOICE_SCANNING_PLAN.md.
///
/// Manager-only. Status transitions:
///   parsing → review → committed
///                   ↘ discarded
/// </summary>
public class InvoiceDocument
{
    public int Id { get; set; }

    /// <summary>Supplier's invoice number, e.g. "2600141367".</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    public string SupplierName { get; set; } = string.Empty;
    public string? SupplierIco { get; set; }
    public string? SupplierIcDph { get; set; }
    public string? SupplierIban { get; set; }

    /// <summary>dátum vyhotovenia — when the invoice was issued.</summary>
    public DateTime IssueDate { get; set; }

    /// <summary>dátum dodania (header-level), if printed on the document.</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>dátum splatnosti — payment due date.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>obdobie plnenia (from–to range), if printed.</summary>
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }

    /// <summary>EUR only in V1; reserved for future multi-currency.</summary>
    public string Currency { get; set; } = "EUR";

    // ─── Reconciliation invariants ──────────────────────────────────
    // The save endpoint requires the sum of MaterialPurchase subtotals + VAT
    // to equal TotalInclVat to the cent. See INVOICE_SCANNING_PLAN.md
    // §"Reconciliation rule".

    /// <summary>cena bez DPH on the printed invoice.</summary>
    public decimal TotalExclVat { get; set; }

    /// <summary>Sum of všetkých DPH amounts across rates.</summary>
    public decimal TotalVat { get; set; }

    /// <summary>spolu / k úhrade — the printed grand total.</summary>
    public decimal TotalInclVat { get; set; }

    /// <summary>Cloudinary URL of the original uploaded PDF (immutable).</summary>
    public string PdfUrl { get; set; } = string.Empty;

    /// <summary>
    /// Full Document AI response, preserved verbatim. The canonical "what the
    /// document said" — survives any future re-parse, audit, or schema migration.
    /// </summary>
    public string RawOcrJson { get; set; } = string.Empty;

    /// <summary>parsing | review | committed | discarded.</summary>
    public string Status { get; set; } = "parsing";

    /// <summary>"invoice" | "receipt" (pokladničný blok) — set at parse time
    /// from eKasa markers so the list can filter invoices from receipts.</summary>
    public string DocumentKind { get; set; } = "invoice";

    // ─── Divisions (CUSTOMER_ROADMAP Fáza D) ────────────────────────
    // The company runs two divisions; every document belongs to exactly one
    // and carries a money direction. The division pages show mesiac +
    // Príjem / Výdaj / Rozdiel computed from these two fields.

    /// <summary>"profistav" (stavby) | "stroje". Chosen at upload via the
    /// division switcher; existing documents default to profistav.</summary>
    public string Division { get; set; } = "profistav";

    /// <summary>"cost" (výdaj) | "income" (príjem — AZ issued the invoice,
    /// e.g. machine work done for someone). Auto-detected at parse time when
    /// AZ Profistav appears as the SUPPLIER; manually togglable on review.</summary>
    public string Direction { get; set; } = "cost";

    /// <summary>Optional INFORMATIONAL backtrack (F1): which mašina this cost
    /// belongs to ("aby vedeli"). Division money never computes on it (D4).
    /// Mutually exclusive with <see cref="CarId"/>.</summary>
    public int? MachineId { get; set; }
    /// <summary>Optional informational backtrack to a vehicle — same rules.</summary>
    public int? CarId { get; set; }

    public Machine? Machine { get; set; }
    public Car? Car { get; set; }

    /// <summary>True only when server-side reconciliation succeeded.</summary>
    public bool ReconciliationOk { get; set; } = false;

    /// <summary>Human-readable description of the last reconciliation check.</summary>
    public string? ReconciliationNote { get; set; }

    /// <summary>JWT username of the manager who uploaded the PDF.</summary>
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public string? CommittedBy { get; set; }
    public DateTime? CommittedAt { get; set; }

    /// <summary>Free-text the manager can attach (e.g. internal accounting note).</summary>
    public string? Note { get; set; }

    /// <summary>
    /// How this invoice arrived in the system: <c>"file"</c> when the
    /// manager picked a PDF or image via the file-picker, <c>"camera"</c>
    /// when the manager photographed the paper invoice in the in-app
    /// scanner. Defaults to <c>"file"</c> for back-compat with rows that
    /// existed before V1.1. See INVOICE_SCANNING_CAMERA_PLAN.md.
    /// </summary>
    public string ScanSource { get; set; } = "file";

    /// <summary>
    /// When <see cref="ScanSource"/> is <c>"camera"</c>, the number of
    /// photos the manager took before submitting. Null otherwise (the
    /// PDF already knows its own page count).
    /// </summary>
    public int? ScanPageCount { get; set; }

    public ICollection<MaterialPurchase> Purchases { get; set; } = [];
}
