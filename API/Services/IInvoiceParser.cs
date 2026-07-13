namespace API.Services;

/// <summary>
/// Maps a <see cref="DocumentAiResult"/> into our domain shape: one invoice
/// header, N delivery-list groups (each ≈ one MaterialPurchase), each with
/// its own line items.
///
/// The Document AI Invoice Parser returns line items as a flat list — it does
/// NOT preserve the "za dodací list" grouping the supplier uses. We recover
/// the grouping by scanning the full document text for the SK-specific
/// `za dodací list DL-XXX ... akcia: SITE` pattern and assigning each
/// line item to the most-recent group based on text-anchor offsets.
///
/// Finance-grade: any extraction that fails returns the line / group with
/// null fields and a confidence below 0.5 so the review screen flags it.
/// We never invent numbers.
/// </summary>
public interface IInvoiceParser
{
    ParsedInvoice Parse(DocumentAiResult ocr);
}

public sealed record ParsedInvoice(
    ParsedInvoiceHeader Header,
    IReadOnlyList<ParsedDeliveryList> DeliveryLists);

public sealed record ParsedInvoiceHeader(
    string? InvoiceNumber,
    string? SupplierName,
    string? SupplierIco,
    string? SupplierIcDph,
    string? SupplierIban,
    DateTime? IssueDate,
    DateTime? DeliveryDate,
    DateTime? DueDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    decimal? TotalExclVat,
    decimal? TotalVat,
    decimal? TotalInclVat,
    string Currency,
    /// <summary>True for cash-register receipts (pokladničný blok) — detected
    /// from eKasa markers (KP/KPEKK code, "NA ÚHRADU EUR"/"Spolu v EUR").</summary>
    bool IsReceipt = false);

public sealed record ParsedDeliveryList(
    /// <summary>e.g. "DL-100-26-015474" or null when no DL ref was found.</summary>
    string? DeliveryNoteRef,
    /// <summary>Free-text site name from "akcia: <name>", e.g. "Devinska". Null when blank or ".".</summary>
    string? AkciaName,
    /// <summary>Free-text picker name from "prevzal:".</summary>
    string? PickedUpBy,
    /// <summary>Free-text "Pozn.DL:" remark.</summary>
    string? Note,
    /// <summary>Date from the delivery-list header.</summary>
    DateTime? DeliveryDate,
    /// <summary>Sum of this delivery list's lines excl. VAT, from the printed subtotal.</summary>
    decimal? SubtotalExclVat,
    /// <summary>Sum of this delivery list's VAT amounts.</summary>
    decimal? SubtotalVat,
    IReadOnlyList<ParsedLine> Lines);

public sealed record ParsedLine(
    string? SupplierItemCode,
    string Description,
    decimal? Quantity,
    string? Unit,
    decimal? ListPriceExclVat,
    decimal? DiscountPercent,
    decimal? UnitPriceExclVat,
    decimal? UnitPriceInclVat,
    decimal? LineTotalExclVat,
    decimal VatRate,
    bool IsReverseCharge,
    bool IsService,
    float Confidence);
