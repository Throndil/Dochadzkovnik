using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace API.Services;

/// <summary>
/// Vision-LLM invoice extraction — the safety net behind the deterministic
/// parser. A photo scan that Document AI scrambles (columns shuffled, rows
/// split) is usually trivially readable for a multimodal model because it
/// SEES the layout instead of reflowing it.
///
/// Finance-grade discipline is preserved by the CALLER: whatever this
/// returns must pass the same arithmetic reconciliation gate as the
/// deterministic parse (lines + VAT ≈ printed total) or it is discarded.
/// The model is explicitly instructed to never invent values — but the gate,
/// not the instruction, is the guarantee.
/// </summary>
public interface ILlmInvoiceExtractor
{
    bool IsConfigured { get; }

    /// <summary>
    /// Gemini-first pipeline: the vision read runs BEFORE Document AI and,
    /// when it passes the reconciliation gate, Document AI is skipped
    /// (~50× cheaper per document). Default true when configured; set
    /// <c>Gemini:Primary=false</c> as the kill switch to restore the
    /// deterministic-first order.
    /// </summary>
    bool IsPrimary { get; }

    /// <summary>
    /// Extract the invoice from the original file (PDF or image). Returns
    /// null when unconfigured, on any API failure, or when the response
    /// can't be mapped — callers fall back to the deterministic parse.
    /// </summary>
    Task<ParsedInvoice?> ExtractAsync(byte[] fileBytes, string mimeType, string? ocrText, CancellationToken ct = default);
}

/// <summary>
/// Google Gemini implementation (generativelanguage.googleapis.com). Chosen
/// because the customer's invoice data already flows to Google via Document
/// AI — no new data processor. Configure with <c>Gemini:ApiKey</c>
/// (+ optional <c>Gemini:Model</c>, default gemini-3.5-flash — override in
/// config when Google ships the next generation; nothing else changes).
/// Structured JSON output via responseSchema, temperature 0.
/// </summary>
public sealed class GeminiInvoiceExtractor : ILlmInvoiceExtractor
{
    private readonly HttpClient _http;
    private readonly ILogger<GeminiInvoiceExtractor> _log;
    private readonly ScanStatusService _status;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly bool _primary;

    public GeminiInvoiceExtractor(HttpClient http, IConfiguration cfg, ScanStatusService status, ILogger<GeminiInvoiceExtractor> log)
    {
        _http = http;
        _status = status;
        _log = log;
        _apiKey = cfg["Gemini:ApiKey"];
        // Flash-Lite default: reading a printed invoice is far below any
        // current model's ceiling, and the lite tier has the most free-tier
        // headroom (the flagship flash sheds free-tier traffic first under
        // load — live: 503 UNAVAILABLE "high demand" on every call).
        _model = string.IsNullOrWhiteSpace(cfg["Gemini:Model"]) ? "gemini-3.1-flash-lite" : cfg["Gemini:Model"]!;
        _primary = !string.Equals(cfg["Gemini:Primary"], "false", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public bool IsPrimary => IsConfigured && _primary;

    public async Task<ParsedInvoice?> ExtractAsync(byte[] fileBytes, string mimeType, string? ocrText, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            var prompt =
                "You are extracting a Slovak supplier invoice or cash-register receipt (pokladničný blok / bloček) for accounting. The entire document is in Slovak.\n" +
                "Rules — follow ALL of them:\n" +
                "1. Extract ONLY what is printed. If a value is unreadable or absent, use null. NEVER guess or invent numbers.\n" +
                "2. 'AZ Profistav' (IČO 47208368) is always the CUSTOMER (odberateľ). The supplier (dodávateľ) is the OTHER company.\n" +
                "3. All money values are numbers with a dot decimal separator. Dates are YYYY-MM-DD.\n" +
                "4. lineTotalExclVat is the line total WITHOUT VAT. Cash receipts print line prices WITH VAT — divide by (1 + vatRatePercent/100) and round to 2 decimals in that case.\n" +
                "5. totalInclVat is the printed grand total actually payable (po zaokrúhlení when the receipt shows rounding).\n" +
                "6. If the invoice groups items under delivery notes ('za dodací list DL-…'), create one deliveryLists entry per group with its reference and site name ('akcia'); otherwise return a single group with deliveryNoteRef null.\n" +
                "7. vatRatePercent 0 means reverse charge (prenesenie daňovej povinnosti).\n" +
                "8. RECONCILE — the printed grand total (totalInclVat) is ground truth and is usually correct even when the line items are hard to read; the lines are what you most often get wrong. After extracting the lines, check that the sum of every lineTotalExclVat plus its VAT equals totalInclVat (allow a few cents for rounding). If it does NOT match, the LINES are wrong: re-read their quantities, unit prices, discountPercent and vatRatePercent from the image and correct them so they reconcile to the printed total. NEVER change totalInclVat to fit the lines, and NEVER invent, split or pad line items just to reach the total — if the printed lines genuinely will not reconcile, keep your best honest reading of them and leave totalInclVat as printed.\n" +
                "9. Discounts: if a line shows a 'Zľava' (a percent or an amount off), lineTotalExclVat is the value AFTER the discount; record the percentage in discountPercent.\n" +
                "10. On receipts each item's net base is often printed as 'Základ' beneath the gross price — use that printed 'Základ' as lineTotalExclVat when it is shown. Summary rows ('Medzisúčet', 'Zaokrúhlenie', 'Na úhradu', 'Spolu', 'Hotovosť', VAT-rate recap tables) are NOT items — never create line entries for them.\n" +
                (string.IsNullOrWhiteSpace(ocrText)
                    ? ""
                    : "\nFor reference, an OCR pass produced this (possibly scrambled) text layer:\n---\n" + Truncate(ocrText, 12000) + "\n---\n") +
                "Read the attached document and return the JSON.";

            var body = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { inline_data = new { mime_type = mimeType, data = Convert.ToBase64String(fileBytes) } },
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0,
                    responseMimeType = "application/json",
                    responseSchema = Schema,
                    // Give the vision model the largest per-page token budget so
                    // dense line-item / receipt text stays legible. Free-tier
                    // token cost is negligible per document.
                    mediaResolution = "MEDIA_RESOLUTION_HIGH"
                }
            };

            var requestJson = JsonSerializer.Serialize(body);

            // Overload resilience: 503 "high demand" / 429 quota spikes are
            // transient — retry once after a pause, then fall back to the
            // lite model (which has the most free-tier headroom). Any other
            // error is terminal for this document; callers keep the
            // deterministic parse.
            var models = new[] { _model, "gemini-3.1-flash-lite" }.Distinct();
            foreach (var model in models)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var req = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_apiKey}")
                    {
                        Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                    };
                    using var resp = await _http.SendAsync(req, ct);
                    var respText = await resp.Content.ReadAsStringAsync(ct);

                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(respText);
                        var json = doc.RootElement
                            .GetProperty("candidates")[0]
                            .GetProperty("content")
                            .GetProperty("parts")[0]
                            .GetProperty("text")
                            .GetString();
                        if (string.IsNullOrWhiteSpace(json)) return null;
                        // Diagnostic: the exact JSON the model returned, so we can
                        // see whether missing line data is the model's fault or ours.
                        _log.LogInformation("[InvoiceScanning] Gemini raw JSON ({Len} chars): {Json}",
                            json.Length, Truncate(json, 12000));
                        var dto = JsonSerializer.Deserialize<LlmInvoice>(json, JsonOpts);
                        _status.MarkAiOk();
                        return dto == null ? null : Map(dto);
                    }

                    var overloaded = resp.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable
                                                     or System.Net.HttpStatusCode.TooManyRequests;
                    // 429 carrying a per-day quota marker = the FREE TIER is
                    // spent for today — record it so the UI can show the
                    // "beží v záložnom režime, obnoví sa zajtra" banner
                    // instead of the customer wondering why photos parse
                    // worse all afternoon.
                    if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        && respText.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                    {
                        _status.MarkAiQuotaExhausted();
                    }
                    else if (overloaded)
                    {
                        _status.MarkAiTransientFailure();
                    }
                    // 404 = the configured model name doesn't exist (typo /
                    // retired model) — move on to the fallback model instead
                    // of failing the document.
                    var wrongModel = resp.StatusCode == System.Net.HttpStatusCode.NotFound;
                    _log.LogWarning("[InvoiceScanning] Gemini {Model} returned {Status} (attempt {Attempt}): {Body}",
                        model, (int)resp.StatusCode, attempt + 1, Truncate(respText, 300));
                    if (wrongModel) break;
                    if (!overloaded) return null;
                    if (attempt == 0) await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[InvoiceScanning] Gemini extraction failed.");
            return null;
        }
    }

    // ─── Mapping to the domain shape ────────────────────────────────

    private static ParsedInvoice Map(LlmInvoice d)
    {
        var lists = new List<ParsedDeliveryList>();
        foreach (var g in d.DeliveryLists ?? [])
        {
            var lines = new List<ParsedLine>();
            foreach (var l in g.Lines ?? [])
            {
                if (string.IsNullOrWhiteSpace(l.Description)) continue;
                var vat = l.VatRatePercent ?? 23m;
                var qty = l.Quantity is > 0m ? l.Quantity : null;
                var total = l.LineTotalExclVat
                            ?? (qty.HasValue && l.UnitPriceExclVat.HasValue
                                ? Math.Round(qty.Value * l.UnitPriceExclVat.Value, 2, MidpointRounding.AwayFromZero)
                                : (decimal?)null);
                var unitPrice = l.UnitPriceExclVat
                                ?? (qty is > 0m && total.HasValue
                                    ? Math.Round(total.Value / qty.Value, 4, MidpointRounding.AwayFromZero)
                                    : (decimal?)null);
                lines.Add(new ParsedLine(
                    SupplierItemCode: string.IsNullOrWhiteSpace(l.Code) ? null : l.Code.Trim(),
                    Description: l.Description.Trim(),
                    Quantity: qty,
                    Unit: string.IsNullOrWhiteSpace(l.Unit) ? "ks" : l.Unit.Trim(),
                    ListPriceExclVat: null,
                    DiscountPercent: l.DiscountPercent is > 0m ? l.DiscountPercent : null,
                    UnitPriceExclVat: unitPrice,
                    UnitPriceInclVat: null,
                    LineTotalExclVat: total,
                    VatRate: vat,
                    IsReverseCharge: vat == 0m,
                    IsService: false,
                    Confidence: 0.6f));
            }

            // Per-group subtotals: printed values win; otherwise derive from
            // the lines so per-DL reconciliation still works.
            var subExcl = g.SubtotalExclVat ?? (lines.Count > 0 ? lines.Sum(x => x.LineTotalExclVat ?? 0m) : (decimal?)null);
            var subVat = g.SubtotalVat ?? (lines.Count > 0
                ? lines.Sum(x => Math.Round((x.LineTotalExclVat ?? 0m) * x.VatRate / 100m, 2, MidpointRounding.AwayFromZero))
                : (decimal?)null);

            lists.Add(new ParsedDeliveryList(
                DeliveryNoteRef: string.IsNullOrWhiteSpace(g.DeliveryNoteRef) ? null : g.DeliveryNoteRef.Trim(),
                AkciaName: string.IsNullOrWhiteSpace(g.SiteName) ? null : g.SiteName.Trim(),
                PickedUpBy: null,
                Note: null,
                DeliveryDate: ParseDate(g.DeliveryDate),
                SubtotalExclVat: subExcl,
                SubtotalVat: subVat,
                Lines: lines));
        }

        var header = new ParsedInvoiceHeader(
            InvoiceNumber: string.IsNullOrWhiteSpace(d.InvoiceNumber) ? null : d.InvoiceNumber.Trim(),
            SupplierName: string.IsNullOrWhiteSpace(d.SupplierName) ? null : d.SupplierName.Trim(),
            SupplierIco: string.IsNullOrWhiteSpace(d.SupplierIco) ? null : d.SupplierIco.Trim(),
            SupplierIcDph: string.IsNullOrWhiteSpace(d.SupplierIcDph) ? null : d.SupplierIcDph.Trim(),
            SupplierIban: null,
            IssueDate: ParseDate(d.IssueDate),
            DeliveryDate: ParseDate(d.DeliveryDate),
            DueDate: ParseDate(d.DueDate),
            PeriodFrom: null,
            PeriodTo: null,
            TotalExclVat: d.TotalExclVat,
            TotalVat: d.TotalVat,
            TotalInclVat: d.TotalInclVat,
            Currency: "EUR",
            IsReceipt: d.IsReceipt ?? false);

        return new ParsedInvoice(header, lists);
    }

    private static DateTime? ParseDate(string? s)
        => DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d)
            ? d.Date
            : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // ─── Wire DTOs (match the responseSchema below) ─────────────────

    private sealed record LlmInvoice(
        string? SupplierName, string? SupplierIco, string? SupplierIcDph,
        string? InvoiceNumber, string? IssueDate, string? DeliveryDate, string? DueDate,
        decimal? TotalExclVat, decimal? TotalVat, decimal? TotalInclVat,
        bool? IsReceipt, List<LlmGroup>? DeliveryLists);

    private sealed record LlmGroup(
        string? DeliveryNoteRef, string? SiteName, string? DeliveryDate,
        decimal? SubtotalExclVat, decimal? SubtotalVat, List<LlmLine>? Lines);

    private sealed record LlmLine(
        string? Code, string? Description, decimal? Quantity, string? Unit,
        decimal? UnitPriceExclVat, decimal? LineTotalExclVat,
        decimal? VatRatePercent, decimal? DiscountPercent);

    /// <summary>Gemini structured-output schema (OpenAPI subset).</summary>
    private static readonly object Schema = new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["supplierName"] = new { type = "STRING", nullable = true },
            ["supplierIco"] = new { type = "STRING", nullable = true },
            ["supplierIcDph"] = new { type = "STRING", nullable = true },
            ["invoiceNumber"] = new { type = "STRING", nullable = true },
            ["issueDate"] = new { type = "STRING", nullable = true, description = "YYYY-MM-DD" },
            ["deliveryDate"] = new { type = "STRING", nullable = true, description = "YYYY-MM-DD" },
            ["dueDate"] = new { type = "STRING", nullable = true, description = "YYYY-MM-DD" },
            ["totalExclVat"] = new { type = "NUMBER", nullable = true },
            ["totalVat"] = new { type = "NUMBER", nullable = true },
            ["totalInclVat"] = new { type = "NUMBER", nullable = true },
            ["isReceipt"] = new { type = "BOOLEAN", nullable = true },
            ["deliveryLists"] = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object>
                    {
                        ["deliveryNoteRef"] = new { type = "STRING", nullable = true },
                        ["siteName"] = new { type = "STRING", nullable = true },
                        ["deliveryDate"] = new { type = "STRING", nullable = true, description = "YYYY-MM-DD" },
                        ["subtotalExclVat"] = new { type = "NUMBER", nullable = true },
                        ["subtotalVat"] = new { type = "NUMBER", nullable = true },
                        ["lines"] = new
                        {
                            type = "ARRAY",
                            items = new
                            {
                                type = "OBJECT",
                                properties = new Dictionary<string, object>
                                {
                                    ["code"] = new { type = "STRING", nullable = true },
                                    ["description"] = new { type = "STRING" },
                                    ["quantity"] = new { type = "NUMBER", nullable = true },
                                    ["unit"] = new { type = "STRING", nullable = true },
                                    ["unitPriceExclVat"] = new { type = "NUMBER", nullable = true },
                                    ["lineTotalExclVat"] = new { type = "NUMBER", nullable = true },
                                    ["vatRatePercent"] = new { type = "NUMBER", nullable = true },
                                    ["discountPercent"] = new { type = "NUMBER", nullable = true }
                                },
                                required = new[] { "description" }
                            }
                        }
                    }
                }
            }
        },
        required = new[] { "deliveryLists" }
    };
}
