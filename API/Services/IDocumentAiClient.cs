namespace API.Services;

/// <summary>
/// Thin wrapper around Google Document AI's Invoice Parser. Returns the raw
/// response (preserved verbatim on <c>InvoiceDocument.RawOcrJson</c>) plus
/// the parsed entities the caller actually needs.
///
/// Why an interface: invoice scanning is finance-grade and we want a
/// deterministic test seam. Production wires <see cref="DocumentAiClient"/>;
/// tests can substitute a fixture-based fake that returns a canned response.
/// </summary>
public interface IDocumentAiClient
{
    /// <summary>
    /// Sends a PDF or image to the configured Document AI processor and returns
    /// the response. The caller maps the response into our domain (line items,
    /// totals, akcia→Location, etc.) — this client only owns transport.
    /// </summary>
    /// <param name="content">Raw bytes of the PDF or image.</param>
    /// <param name="mimeType">MIME type, e.g. "application/pdf" or "image/jpeg".</param>
    /// <param name="ct">Cancellation token. Caller should set a sensible timeout.</param>
    /// <returns>The Document AI response, serialized to JSON.</returns>
    Task<DocumentAiResult> ProcessAsync(byte[] content, string mimeType, CancellationToken ct = default);
}

/// <summary>
/// Result of a Document AI call. <see cref="RawJson"/> is the canonical
/// "what the OCR returned" — preserved on the InvoiceDocument row so a
/// future audit or re-parse can replay the source.
/// <see cref="Entities"/> is the same data already projected into a
/// dictionary shape the parser logic can consume without a Protobuf dependency.
/// </summary>
public sealed record DocumentAiResult(
    string RawJson,
    IReadOnlyList<DocumentAiEntity> Entities,
    string FullText);

/// <summary>
/// One extracted entity from Document AI. Invoices typically produce
/// <c>invoice_id</c>, <c>supplier_name</c>, <c>total_amount</c>, etc., plus
/// nested <c>line_item</c> entities. Confidence is in [0..1]; values below 0.8
/// are flagged amber on the review screen per the plan.
/// </summary>
public sealed record DocumentAiEntity(
    string Type,
    string? MentionText,
    string? NormalizedValue,
    float Confidence,
    IReadOnlyList<DocumentAiEntity> Properties);
