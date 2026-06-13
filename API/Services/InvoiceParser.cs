using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace API.Services;

/// <summary>
/// Slovak construction-invoice parser. Combines Document AI's structured
/// entities (header fields, line items) with text-pattern scanning for
/// SK-specific concepts the generic Invoice Parser doesn't natively
/// extract: IČO / IČ DPH / IBAN / akcia / dodací list grouping / reverse
/// charge.
///
/// The DEK invoice (FA_2600141367.pdf) is the binding test: 11 delivery
/// lists, 31 lines, totals reconcile to 1 788,43 EUR.
///
/// Design notes:
///  - Numbers are parsed via SlovakNumberHelper so both comma- and dot-
///    decimal inputs round-trip.
///  - We never invent missing values. A field absent from the OCR is null
///    and the manager edits it on the review page.
///  - Grouping by "za dodací list" uses textAnchor offsets when present
///    via Document AI entities, falling back to text scanning otherwise.
/// </summary>
public sealed class InvoiceParser : IInvoiceParser
{
    // Fallback supplier-name extractor. SK invoices put the supplier name on
    // a line near the "dodávateľ" header — sometimes the line directly after,
    // sometimes after a leading logo word. Match the first 5 lines after
    // "dodávateľ" and pick the first one that looks like a company entity
    // (contains "s.r.o." / "a.s." / "k.s." / "s. r. o.").
    private static readonly Regex SupplierFromHeaderRx = new(
        @"dodávateľ(?:\s*\r?\n[^\r\n]*){0,5}\s*\r?\n\s*([^\r\n]*?(?:s\.\s*r\.\s*o\.?|a\.\s*s\.|k\.\s*s\.|spol\.\s*s\s*r\.\s*o\.)[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Even-broader fallback: pick the first line after dodávateľ that's at
    // least 8 characters and contains a letter. Used when no company-suffix
    // matched (e.g. živnostník, individual supplier).
    private static readonly Regex SupplierFromHeaderRxLoose = new(
        @"dodávateľ\s*\r?\n\s*([^\r\n]{8,200})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IcoRx     = new(@"\bIČO\s*:?\s*(\d{6,10})\b", RegexOptions.Compiled);
    private static readonly Regex IcDphRx   = new(@"\bIČ\s*DPH\s*:?\s*([A-Z]{2}\d{8,12})\b", RegexOptions.Compiled);
    private static readonly Regex IbanRx    = new(@"\bIBAN\s*:?\s*([A-Z]{2}\d{2}(?:\s*\d){10,30})\b", RegexOptions.Compiled);
    private static readonly Regex PeriodRx  = new(@"obdobie\s+plnenia\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})\s*[-–]\s*(\d{1,2}\.\d{1,2}\.\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InvoiceNumberRx = new(@"(?:Súhrnná\s+faktúra|Faktúra)\s+(\d{6,16})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Safety net when the number is not directly adjacent to "Faktúra"
    // (HEKTRANS prints it top-right, two lines below the label): take the
    // first 6-16 digit run within ~120 chars after "Faktúra" or a
    // "Variab. symb." / "variabilný symbol" label.
    private static readonly Regex InvoiceNumberNearRx = new(@"(?:\bfaktúra\b|variab\w*\.?\s*symb\w*\.?)[\s\S]{0,120}?\b(\d{6,16})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "za dodací list DL-100-26-015474 zo dňa 19.5.2026 | ... | akcia: Devinska | ... | dátum dodania ...: 19.05.2026 | Pozn.DL: ..."
    private static readonly Regex DeliveryListRx = new(
        @"za\s+dodací\s+list\s+([A-Z0-9\-]+)(?<rest>[\s\S]*?)(?=za\s+dodací\s+list|cena\s+bez\s+DPH|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AkciaRx     = new(@"akcia\s*:?\s*([^|\r\n]+?)(?=\s*\||\s*\||\s*\r|\s*\n|\s*$|\s*dátum\s+dodania)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PrevzalRx   = new(@"prevzal\s*:?\s*([^|\r\n]+?)(?=\s*\||\s*\r|\s*\n|\s*$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PoznDlRx    = new(@"Pozn\.DL\s*:?\s*([^\r\n]+?)(?=\s*$|\s*\r|\s*\n)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DlDateRx    = new(@"dátum\s+dodania(?:\s+tovaru\s+alebo\s+služby)?\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SubtotalRx  = new(@"základ\s+DPH\s+(\d{1,2})\s*%\s*([\d\s.,]+?)\s*EUR\s*\|?\s*DPH\s+\1\s*%\s*([\d\s.,]+?)\s*EUR", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Issue / due / delivery date in the header. SK format `d.M.yyyy`.
    // The 5-column price block at the end of every line-item row on a DEK
    // (and most other SK construction supplier) invoice:
    //   <list_price>  <discount>,<frac>%  <post_discount_excl>  <post_discount_incl>  <line_total>
    // Slovak numbers use comma decimal and may have an internal space for the
    // thousands separator. Discount % can be "20,00" or just "20". We use this
    // to OVERRIDE Document AI's per-row prices because Document AI's column
    // mapping is unreliable on this layout (picks list price for some rows,
    // discounted price for others). The `-?` prefix on each number supports
    // credit / "Zľava z prenájmu" rows where the total is negative.
    private static readonly Regex LinePricesRx = new(
        @"(?<list>-?\d+(?:[\s ]\d{3})*,\d+)\s+(?<discount>-?\d+(?:,\d+)?)\s*%\s+(?<postExcl>-?\d+(?:[\s ]\d{3})*,\d+)\s+(?<postIncl>-?\d+(?:[\s ]\d{3})*,\d+)\s+(?<total>-?\d+(?:[\s ]\d{3})*,\d+)",
        RegexOptions.Compiled);

    private static readonly Regex IssueDateRx    = new(@"dátum\s+(?:vyhotovenia|vydania)\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DueDateRx      = new(@"dátum\s+splatnosti\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderDelDateRx = new(@"dátum\s+dodania\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Grand totals on the last page. DEK prints "cena bez DPH … EUR".
    // Single-table suppliers (HEKTRANS / Doklado layout) print a summary box
    // where "Základ DPH" is followed by the "Výška DPH" / "Celkom" labels
    // BEFORE the values, so those labels are optionally skipped. The per-DL
    // "základ DPH <rate> % … EUR" subtotal lines cannot match here because
    // the "%" sign blocks the amount capture.
    private static readonly Regex TotalExclVatRx = new(@"(?:cena\s+bez\s+DPH|základ\s+DPH(?:\s*výška\s+DPH)?(?:\s*celkom)?)\s*:?\s*([\d\s.,]+?)\s*(?:EUR|€)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Incl-VAT grand total. Labelled forms first ("Suma na úhradu …",
    // "celkom k úhrade …"). DEK detaches the spolu / k úhrade labels from
    // their values (other text flows between them), so the reliable anchor
    // is the value block itself: the first amount after "zaokrúhlenie" is
    // the rounding, and the amount immediately following it is the "spolu"
    // grand total. The old bare spolu/k úhrade forms either matched nothing
    // or the 0,00 rounding line, so they were replaced by this anchor.
    private static readonly Regex TotalInclVatRx = new(@"(?:suma\s+na\s+úhradu|celkom\s+k\s+úhrade|zaokrúhlenie[\s\S]*?\d[\d\s.,]*(?:EUR|€))\s*:?\s*(\d[\d\s.,]*?)\s*(?:EUR|€)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ─── Supplier-specific dispatch ────────────────────────────────
    // IČO of suppliers whose invoice layout has a dedicated parser.
    private const string IcoBauArticel = "35919175";   // SunSoft.EcoSun layout
    private const string IcoHektrans   = "36055140";   // Doklado.sk transport layout

    // A "quantity unit" token, e.g. "6,30 t" / "1 234,5 kg". Comma decimals
    // only, so a weighbridge "1.65 t" (dot) on an extra page is ignored.
    private static readonly Regex QtyUnitRx = new(
        @"(\d{1,3}(?:[\s ]\d{3})*,\d+)\s*(t|kg|ks|m3|m²|m2|m|l|bal|hod|km|bm|pal)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ParsedInvoice Parse(DocumentAiResult ocr)
    {
        var text = ocr.FullText ?? string.Empty;

        // ── Header ──────────────────────────────────────────────────
        var header = ParseHeader(text, ocr.Entities);

        // ── Delivery-list segmentation ──────────────────────────────
        // Supplier-specific layouts where Document AI's generic extraction is
        // poor (missing quantities / unit prices, phantom total rows) get a
        // dedicated text parser keyed by IČO. Everything else uses the general
        // parser. Additive — unknown suppliers are completely unaffected.
        //
        // Otherwise: we segment the text by "za dodací list" occurrences, then
        // for each segment extract metadata + match Document AI line_item
        // entities by their textAnchor offsets when available, or by text
        // proximity otherwise.
        var deliveryLists = TryParseBySupplier(header.SupplierIco, text, ocr.Entities, header)
                            ?? ParseDeliveryLists(text, ocr.Entities);

        // ── Post-correction ──────────────────────────────────────────
        // Document AI sometimes returns the same amount for visually-
        // similar adjacent rows (e.g. the PN10010 rental + PS01090 credit
        // pair on DL-100-26-015519 of the DEK invoice). When that breaks
        // the section's arithmetic, infer the wrong row's total from
        // (subtotal − sum-of-other-rows). The printed subtotal is the
        // most reliable signal Document AI gave us.
        deliveryLists = InferDuplicateLineTotalsFromSubtotal(deliveryLists);

        // No "za dodací list" grouping (single-table suppliers like
        // HEKTRANS): the one synthetic segment has no printed "základ DPH
        // X% … | DPH X% …" subtotal, so carry the header summary-box totals
        // onto it instead of leaving them null.
        if (deliveryLists.Count == 1 && deliveryLists[0].DeliveryNoteRef == null
            && !deliveryLists[0].SubtotalExclVat.HasValue
            && (header.TotalExclVat.HasValue || header.TotalVat.HasValue))
        {
            deliveryLists = new List<ParsedDeliveryList>
            {
                deliveryLists[0] with
                {
                    SubtotalExclVat = header.TotalExclVat,
                    SubtotalVat = header.TotalVat
                }
            };
        }

        return new ParsedInvoice(header, deliveryLists);
    }

    /// <summary>
    /// Route to a supplier-specific line parser by IČO, or null to fall back to
    /// the general parser. Digits-only comparison so "SK..."/spaces don't matter.
    /// </summary>
    private IReadOnlyList<ParsedDeliveryList>? TryParseBySupplier(
        string? ico, string text, IReadOnlyList<DocumentAiEntity> entities, ParsedInvoiceHeader header)
    {
        var digits = new string((ico ?? "").Where(char.IsDigit).ToArray());
        return digits switch
        {
            IcoBauArticel => ParseSunSoftDeliveryLists(text, entities, header),
            IcoHektrans   => ParseHektransDeliveryLists(text, entities, header),
            _ => null
        };
    }

    /// <summary>
    /// HEKTRANS / Doklado.sk transport layout (IČO 36055140). Columns are
    /// Názov | Počet | MJ | J.cena | Cena | DPH% | DPH | Celkom. Document AI
    /// reads quantity + unit_price correctly, but its "amount" is the WITH-VAT
    /// Celkom (e.g. 369,00), not the excl total — and it emits phantom
    /// line_items for description fragments and the grand total. So we keep
    /// only line_items that have a quantity AND a unit price, and compute the
    /// excl line total as qty × unit price. Falls back to the general parser
    /// when nothing usable is found.
    /// </summary>
    private IReadOnlyList<ParsedDeliveryList> ParseHektransDeliveryLists(
        string text, IReadOnlyList<DocumentAiEntity> entities, ParsedInvoiceHeader header)
    {
        var items = entities
            .Where(e => string.Equals(e.Type, "line_item", StringComparison.OrdinalIgnoreCase))
            .Select(e => new
            {
                Name = string.Join(" ", new[] { Prop(e, "product_code"), Prop(e, "description") }
                           .Where(s => !string.IsNullOrWhiteSpace(s))).Trim(),
                Qty = SlovakNumberHelper.TryParse(Prop(e, "quantity")),
                UnitPrice = SlovakNumberHelper.TryParse(Prop(e, "unit_price")),
                Unit = Prop(e, "unit")
            })
            .Where(x => x.Qty is { } q && q != 0m && x.UnitPrice.HasValue)
            .ToList();

        if (items.Count == 0)
            return ParseDeliveryLists(text, entities);

        var lines = items.Select(x =>
        {
            var total = Math.Round(x.Qty!.Value * x.UnitPrice!.Value, 2, MidpointRounding.AwayFromZero);
            return new ParsedLine(
                SupplierItemCode: null,
                Description: string.IsNullOrWhiteSpace(x.Name) ? "(bez popisu)" : x.Name,
                Quantity: x.Qty,
                Unit: string.IsNullOrWhiteSpace(x.Unit) ? "ks" : x.Unit!.Trim(),
                ListPriceExclVat: x.UnitPrice,
                DiscountPercent: null,
                UnitPriceExclVat: x.UnitPrice,
                UnitPriceInclVat: null,
                LineTotalExclVat: total,
                VatRate: 23m,
                IsReverseCharge: false,
                IsService: true,   // transport / rental service, not stock material
                Confidence: 0.5f);
        }).ToList();

        var subtotalExcl = lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var subtotalVat  = Math.Round(subtotalExcl * 0.23m, 2, MidpointRounding.AwayFromZero);

        return new List<ParsedDeliveryList>
        {
            new ParsedDeliveryList(
                DeliveryNoteRef: null,
                AkciaName: null,
                PickedUpBy: null,
                Note: null,
                DeliveryDate: header.DeliveryDate,
                SubtotalExclVat: subtotalExcl,
                SubtotalVat: subtotalVat,
                Lines: lines)
        };
    }

    /// <summary>
    /// SunSoft.EcoSun invoice layout (e.g. BAU-ARTICEL, IČO 35919175). Document
    /// AI reliably gives each line's description + total (Celkom bez DPH) but
    /// not the quantity or unit price, and emits phantom line_items for the
    /// repeated grand total. So: keep only line_items that have a description,
    /// read the "qty unit" tokens from the text in order, pair them by index,
    /// and derive the unit price as total ÷ qty. One delivery list (no
    /// per-delivery-note grouping on these invoices). Falls back to the general
    /// parser when nothing usable is found.
    /// </summary>
    private IReadOnlyList<ParsedDeliveryList> ParseSunSoftDeliveryLists(
        string text, IReadOnlyList<DocumentAiEntity> entities, ParsedInvoiceHeader header)
    {
        var items = entities
            .Where(e => string.Equals(e.Type, "line_item", StringComparison.OrdinalIgnoreCase))
            .Select(e => new
            {
                Desc = e.Properties.FirstOrDefault(p => p.Type.EndsWith("description", StringComparison.OrdinalIgnoreCase))?.MentionText?.Trim(),
                Total = SlovakNumberHelper.TryParse(e.Properties.FirstOrDefault(p => p.Type.EndsWith("amount", StringComparison.OrdinalIgnoreCase))?.MentionText)
            })
            // Drop the phantom grand-total rows (no description).
            .Where(x => !string.IsNullOrWhiteSpace(x.Desc) && x.Total is { } t && t != 0m)
            .ToList();

        if (items.Count == 0)
            return ParseDeliveryLists(text, entities);   // nothing usable → general parser

        // "qty unit" tokens in document order, e.g. ("6,30","t"), ("1,65","t").
        var qtys = QtyUnitRx.Matches(text)
            .Select(m => new { Qty = SlovakNumberHelper.TryParse(m.Groups[1].Value), Unit = m.Groups[2].Value.Trim().ToLowerInvariant() })
            .Where(x => x.Qty.HasValue)
            .ToList();

        var lines = new List<ParsedLine>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var total = items[i].Total!.Value;
            var qty   = i < qtys.Count ? qtys[i].Qty : null;
            var unit  = i < qtys.Count ? qtys[i].Unit : null;
            decimal? unitPrice = qty is { } q && q != 0m
                ? Math.Round(total / q, 4, MidpointRounding.AwayFromZero)
                : null;

            lines.Add(new ParsedLine(
                SupplierItemCode: null,
                Description: items[i].Desc!,
                Quantity: qty,
                Unit: string.IsNullOrEmpty(unit) ? "ks" : unit,
                ListPriceExclVat: unitPrice,
                DiscountPercent: null,
                UnitPriceExclVat: unitPrice,
                UnitPriceInclVat: null,
                LineTotalExclVat: total,
                VatRate: 23m,
                IsReverseCharge: false,
                IsService: false,
                Confidence: 0.5f));
        }

        var subtotalExcl = lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var subtotalVat  = Math.Round(subtotalExcl * 0.23m, 2, MidpointRounding.AwayFromZero);

        return new List<ParsedDeliveryList>
        {
            new ParsedDeliveryList(
                DeliveryNoteRef: null,
                AkciaName: null,
                PickedUpBy: null,
                Note: null,
                DeliveryDate: header.DeliveryDate,
                SubtotalExclVat: subtotalExcl,
                SubtotalVat: subtotalVat,
                Lines: lines)
        };
    }

    /// <summary>
    /// When a delivery list's lines don't sum to its printed subtotal AND
    /// at least one pair of lines shares the same total (signal of an OCR
    /// duplication), recompute the LATER duplicate's total as
    /// <c>subtotal − sum(other lines)</c>. Conservative — only kicks in
    /// when both conditions hold; otherwise the data is left untouched
    /// and the reconciliation banner asks the manager to edit by hand.
    /// </summary>
    private static IReadOnlyList<ParsedDeliveryList> InferDuplicateLineTotalsFromSubtotal(IReadOnlyList<ParsedDeliveryList> dls)
    {
        var corrected = new List<ParsedDeliveryList>(dls.Count);
        foreach (var dl in dls)
        {
            if (!dl.SubtotalExclVat.HasValue || dl.Lines.Count < 2)
            {
                corrected.Add(dl);
                continue;
            }

            var totals = dl.Lines.Select(l => l.LineTotalExclVat ?? 0m).ToArray();
            var sumLines = totals.Sum();
            var diff = sumLines - dl.SubtotalExclVat.Value;
            if (Math.Abs(diff) <= 0.01m)
            {
                corrected.Add(dl);
                continue;
            }

            // Find a duplicate-total pair. Prefer fixing the LATER occurrence
            // since Document AI tends to propagate values downward.
            int wrongIdx = -1;
            for (int i = 0; i < totals.Length && wrongIdx < 0; i++)
            {
                for (int j = i + 1; j < totals.Length; j++)
                {
                    if (totals[i] != 0m && Math.Abs(totals[i] - totals[j]) <= 0.01m)
                    {
                        wrongIdx = j;
                        break;
                    }
                }
            }
            if (wrongIdx < 0)
            {
                // No duplicate pair — can't safely infer which line is wrong.
                corrected.Add(dl);
                continue;
            }

            var otherSum = 0m;
            for (int k = 0; k < totals.Length; k++)
                if (k != wrongIdx) otherSum += totals[k];
            var newTotal = Math.Round(dl.SubtotalExclVat.Value - otherSum, 2, MidpointRounding.AwayFromZero);

            var newLines = dl.Lines.ToList();
            var wrong = newLines[wrongIdx];
            // ParsedLine is a record — use 'with' to rebuild with the corrected total.
            newLines[wrongIdx] = wrong with { LineTotalExclVat = newTotal };
            corrected.Add(dl with { Lines = newLines });
        }
        return corrected;
    }

    /// <summary>
    /// Look for the supplier name in the text block between "dodávateľ" and
    /// "odberateľ" (the supplier section). Picks the first line containing a
    /// company-form suffix (s.r.o., a.s., k.s., spol. s r.o.). Robust to
    /// whatever line breaks Document AI's text extraction produces.
    /// </summary>
    private static string? ExtractSupplierFromBlock(string text)
    {
        var dodIdx = text.IndexOf("dodávateľ", StringComparison.OrdinalIgnoreCase);
        if (dodIdx < 0) return null;
        var odbIdx = text.IndexOf("odberateľ", dodIdx + 1, StringComparison.OrdinalIgnoreCase);
        var endIdx = odbIdx > dodIdx ? odbIdx : Math.Min(dodIdx + 600, text.Length);
        var block = text.Substring(dodIdx, endIdx - dodIdx);

        // Scan each line for a company-suffix match. Multiline regex with
        // ^ matching after every newline.
        foreach (Match m in Regex.Matches(block,
            @"^([^\r\n]*?(?:s\.\s*r\.\s*o\.?|a\.\s*s\.|k\.\s*s\.|spol\.\s*s\s*r\.\s*o\.)[^\r\n]*)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var line = m.Groups[1].Value.Trim();
            if (line.Length >= 8
                && !line.Contains("ďakujem", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("dodávateľ", StringComparison.OrdinalIgnoreCase))
                return line;
        }
        return null;
    }

    // ─── Header parser ─────────────────────────────────────────────

    private ParsedInvoiceHeader ParseHeader(string text, IReadOnlyList<DocumentAiEntity> entities)
    {
        // Prefer Document AI entities for fields it nails (invoice_id, supplier,
        // dates, totals) and use regex as fallback / for SK-specific fields.
        string? invoiceNumber = FindEntity(entities, "invoice_id")?.MentionText?.Trim()
                                ?? InvoiceNumberRx.Match(text).Groups[1].Value.OrNull()
                                ?? InvoiceNumberNearRx.Match(text).Groups[1].Value.OrNull();

        string? supplierName  = FindEntity(entities, "supplier_name")?.MentionText?.Trim();
        // Document AI's supplier-name heuristic occasionally picks the
        // closing line "ďakujeme za Váš nákup" or a single-word logo like
        // "DEK" instead of the actual supplier. Validate and fall back.
        var looksLikeNoise = supplierName != null && (
            supplierName.Contains("ďakujem", StringComparison.OrdinalIgnoreCase)
            || supplierName.Length < 8                          // single short word, e.g. "DEK"
            || !supplierName.Any(char.IsLetter)
            || !supplierName.Contains(' '));                     // no space → not a real company name
        if (string.IsNullOrWhiteSpace(supplierName) || looksLikeNoise)
        {
            // Block-scan approach: look in the text between "dodávateľ" and
            // "odberateľ" for any line containing a company-form suffix.
            // More robust than the line-anchored regexes — works even when
            // Document AI's text concatenates the supplier address onto one
            // line without newlines.
            var fromBlock = ExtractSupplierFromBlock(text);
            if (!string.IsNullOrWhiteSpace(fromBlock))
            {
                supplierName = fromBlock;
            }
            else
            {
                // Last-resort: the legacy regex fallbacks.
                var withSuffix = SupplierFromHeaderRx.Match(text).Groups[1].Value.Trim();
                if (withSuffix.Length > 0) supplierName = withSuffix;
                else
                {
                    var loose = SupplierFromHeaderRxLoose.Match(text).Groups[1].Value.Trim();
                    if (loose.Length > 0) supplierName = loose;
                }
            }
        }

        // IČO / IČ DPH / IBAN — Document AI does NOT extract these on SK invoices
        // by default. Regex-only.
        var icoMatches    = IcoRx.Matches(text);
        var icDphMatches  = IcDphRx.Matches(text);
        // First IČO on the page is usually the supplier (top-left block); receiver
        // IČO appears later. Same for IČ DPH. This isn't bulletproof — manager
        // edits on review.
        string? supplierIco   = icoMatches.Count   > 0 ? icoMatches[0].Groups[1].Value : null;
        string? supplierIcDph = icDphMatches.Count > 0 ? icDphMatches[0].Groups[1].Value : null;
        string? supplierIban  = IbanRx.Match(text).Groups[1].Value.OrNull()?.Replace(" ", "");

        // Dates: prefer Document AI's normalized ISO when present.
        DateTime? issueDate    = ParseEntityDate(entities, "invoice_date")
                                 ?? ParseSkDate(IssueDateRx.Match(text).Groups[1].Value);
        DateTime? dueDate      = ParseEntityDate(entities, "due_date")
                                 ?? ParseSkDate(DueDateRx.Match(text).Groups[1].Value);
        DateTime? deliveryDate = ParseEntityDate(entities, "delivery_date")
                                 ?? ParseSkDate(HeaderDelDateRx.Match(text).Groups[1].Value);

        // obdobie plnenia
        DateTime? periodFrom = null, periodTo = null;
        var periodMatch = PeriodRx.Match(text);
        if (periodMatch.Success)
        {
            periodFrom = ParseSkDate(periodMatch.Groups[1].Value);
            periodTo   = ParseSkDate(periodMatch.Groups[2].Value);
        }

        // Totals: take Document AI entities first, fall back to regex.
        decimal? totalExclVat = SlovakNumberHelper.TryParse(FindEntity(entities, "net_amount")?.MentionText)
                                ?? SlovakNumberHelper.TryParse(TotalExclVatRx.Match(text).Groups[1].Value);
        decimal? totalVat     = SlovakNumberHelper.TryParse(FindEntity(entities, "total_tax_amount")?.MentionText);

        // Grand total (incl. VAT): prefer the explicitly-labelled text total
        // ("celkom k úhrade … EUR" / "suma na úhradu …" / DEK's zaokrúhlenie
        // pattern). It is money-denominated and anchored to the amount-due
        // line, so — unlike Document AI's bare total_amount entity — it can't
        // be hijacked by a stray "TOTAL 1.65 t" printed on an extra page such
        // as a weighbridge ticket scanned together with the invoice. The
        // entity is only a fallback when no labelled total is found in the text.
        decimal? totalInclVat = SlovakNumberHelper.TryParse(TotalInclVatRx.Match(text).Groups[1].Value)
                                ?? SlovakNumberHelper.TryParse(FindEntity(entities, "total_amount")?.MentionText);

        // If VAT total is missing but excl + incl are present, derive it.
        if (totalVat == null && totalExclVat.HasValue && totalInclVat.HasValue)
            totalVat = totalInclVat.Value - totalExclVat.Value;

        var currency = FindEntity(entities, "currency")?.MentionText?.Trim().ToUpperInvariant() ?? "EUR";

        return new ParsedInvoiceHeader(
            InvoiceNumber: invoiceNumber,
            SupplierName: supplierName,
            SupplierIco: supplierIco,
            SupplierIcDph: supplierIcDph,
            SupplierIban: supplierIban,
            IssueDate: issueDate,
            DeliveryDate: deliveryDate,
            DueDate: dueDate,
            PeriodFrom: periodFrom,
            PeriodTo: periodTo,
            TotalExclVat: totalExclVat,
            TotalVat: totalVat,
            TotalInclVat: totalInclVat,
            Currency: currency);
    }

    // ─── Delivery-list parser ──────────────────────────────────────

    private IReadOnlyList<ParsedDeliveryList> ParseDeliveryLists(string text, IReadOnlyList<DocumentAiEntity> entities)
    {
        var result = new List<ParsedDeliveryList>();

        // Document AI returns line_item entities at the top level. Filter out
        // junk: subtotal rows, DL header phantoms, EULA paragraphs — see
        // IsLikelyJunkLine for the full set.
        var allLineItems = entities
            .Where(e => e.Type == "line_item" && !IsLikelyJunkLine(e))
            .ToList();

        // Segment the text by "za dodací list" occurrences.
        var segments = new List<(int Start, int End, string Body)>();
        var matches  = DeliveryListRx.Matches(text);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            segments.Add((m.Index, m.Index + m.Length, m.Value));
        }
        if (segments.Count == 0)
        {
            segments.Add((0, text.Length, text));
        }

        // Pre-compute each line_item's text offset using a forward-moving
        // cursor. This is critical for invoices where the same product_code
        // appears in multiple delivery lists (e.g. SECA lata 3020200254
        // in DL-015498 AND DL-015499 on the DEK invoice). A naive IndexOf
        // would route both rows to the first segment.
        var offsets = new List<int>(allLineItems.Count);
        int searchCursor = 0;
        foreach (var li in allLineItems)
        {
            var off = FindNextOffset(li, text, searchCursor);
            offsets.Add(off);
            if (off >= 0) searchCursor = off + 1;  // advance past this match
        }

        // Multi-page scan guard: drop line_items that sit on a page with no
        // currency/price tokens (e.g. the weighing-ticket page 2 of the
        // BAU-ARTICEL scan, which otherwise injects phantom lines). Pages
        // are the \f-separated chunks of the text layer; with no \f the
        // whole document is one page and real invoices always carry price
        // tokens, so the guard is a no-op.
        for (int i = allLineItems.Count - 1; i >= 0; i--)
        {
            if (offsets[i] >= 0 && !PageHasPriceTokens(text, offsets[i]))
            {
                allLineItems.RemoveAt(i);
                offsets.RemoveAt(i);
            }
        }

        // Group line items into segments. Each segment's "data range" runs
        // from its "za dodací list" header to the END of its last
        // "základ DPH X% Y EUR | DPH X% Z EUR" subtotal block. Anything
        // after that subtotal is footer text (EULA, payment info, ďakujeme,
        // etc.) and any line_items found there are noise — drop them.
        // This is what kills the 10+ EULA "rows" on DL-100-26-016155.
        for (int s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            var meta = ParseDeliveryListMeta(seg.Body);
            int nextStart = (s + 1 < segments.Count) ? segments[s + 1].Start : text.Length;

            // segmentDataEnd: the position right after the LAST subtotal block
            // inside this segment. Falls back to nextStart when no subtotal is
            // found (shouldn't happen on a well-formed invoice, but be safe).
            int segmentDataEnd = nextStart;
            var subtotalsInSeg = SubtotalRx.Matches(seg.Body);
            if (subtotalsInSeg.Count > 0)
            {
                var last = subtotalsInSeg[^1];
                segmentDataEnd = Math.Min(nextStart, seg.Start + last.Index + last.Length);
            }

            var linesForThisGroup = new List<ParsedLine>();
            for (int i = 0; i < allLineItems.Count; i++)
            {
                var off = offsets[i];
                if (off < seg.Start || off >= segmentDataEnd) continue;

                // Price-search range: from this line's offset to the segment's
                // data end. Using the WHOLE segment (not just up to the next
                // line_item) lets the regex find the price block even when a
                // line's text wraps across multiple newlines — the rental rows
                // PN10010 fail with a tight rowEnd because Document AI splits
                // the quantity/unit from the price block onto separate lines.
                int rowEnd = segmentDataEnd;

                linesForThisGroup.Add(MapLineItem(
                    allLineItems[i], meta.DefaultVatRate, meta.HasReverseCharge,
                    text, off, rowEnd));
            }

            result.Add(new ParsedDeliveryList(
                DeliveryNoteRef: meta.DlRef,
                AkciaName: meta.Akcia,
                PickedUpBy: meta.Prevzal,
                Note: meta.Pozn,
                DeliveryDate: meta.Date,
                SubtotalExclVat: meta.SubExclVat,
                SubtotalVat: meta.SubVat,
                Lines: linesForThisGroup));
        }

        return result;
    }

    /// <summary>
    /// Try to read all 5 price columns from the row of text where this line
    /// item lives. Returns null if the regex doesn't match (Document AI's
    /// values are then the fallback).
    /// </summary>
    private static (decimal list, decimal discount, decimal postExcl, decimal postIncl, decimal total)? ExtractRowPrices(string text, int rowStart, int rowEnd)
    {
        if (rowStart < 0 || rowStart >= text.Length) return null;
        var len = Math.Min(rowEnd, text.Length) - rowStart;
        if (len <= 0) return null;
        var row = text.Substring(rowStart, len);

        var m = LinePricesRx.Match(row);
        if (!m.Success) return null;

        var list     = SlovakNumberHelper.TryParse(m.Groups["list"].Value);
        var discount = SlovakNumberHelper.TryParse(m.Groups["discount"].Value);
        var postExcl = SlovakNumberHelper.TryParse(m.Groups["postExcl"].Value);
        var postIncl = SlovakNumberHelper.TryParse(m.Groups["postIncl"].Value);
        var total    = SlovakNumberHelper.TryParse(m.Groups["total"].Value);
        if (list == null || discount == null || postExcl == null || postIncl == null || total == null) return null;
        return (list.Value, discount.Value, postExcl.Value, postIncl.Value, total.Value);
    }

    // Currency/price tokens: € / EUR / a comma-decimal amount like 183,49.
    private static readonly Regex PriceTokenRx = new(@"€|\bEUR\b|\d+,\d{2}\b", RegexOptions.Compiled);

    /// <summary>
    /// True when the \f-delimited page containing <paramref name="offset"/>
    /// has at least one currency/price token. A page without any (e.g. a
    /// scanned weighing ticket) cannot hold real invoice lines.
    /// </summary>
    private static bool PageHasPriceTokens(string text, int offset)
    {
        if (offset >= text.Length) offset = text.Length - 1;
        int pageStart = offset > 0 ? text.LastIndexOf('\f', offset - 1) + 1 : 0;
        int pageEnd = text.IndexOf('\f', offset);
        if (pageEnd < 0) pageEnd = text.Length;
        return PriceTokenRx.IsMatch(text.Substring(pageStart, pageEnd - pageStart));
    }

    /// <summary>
    /// True when a Document AI "line_item" entity isn't actually a product
    /// line — it's a subtotal row, a per-delivery header, or footer/EULA
    /// text that landed in the line-item column structure. All three need to
    /// be filtered before we persist or render.
    /// </summary>
    private static bool IsLikelyJunkLine(DocumentAiEntity li)
    {
        var code = li.Properties.FirstOrDefault(p => p.Type.EndsWith("/product_code", StringComparison.Ordinal))?.MentionText;
        var descr = (li.Properties.FirstOrDefault(p => p.Type.EndsWith("/description", StringComparison.Ordinal))?.MentionText
                     ?? li.MentionText ?? "").Trim();

        // 1) Subtotal markers.
        if (descr.Contains("základ DPH", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(descr, @"\bDPH\s+\d{1,2}\s*%", RegexOptions.IgnoreCase)) return true;

        // 2) Delivery-list HEADER text that Document AI sometimes returns as
        //    a line. The DEK invoice's DL-015519 phantom row had a description
        //    starting "za dodací list DL-100-26-015519 zo dňa 19.5.2026 |
        //    pobočka: Bratis". Catch any of these markers.
        if (descr.Contains("za dodací list", StringComparison.OrdinalIgnoreCase)) return true;
        if (descr.Contains("pobočka:",  StringComparison.OrdinalIgnoreCase)) return true;
        if (descr.Contains("Pozn.DL:",  StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(descr, @"\bakcia\s*:", RegexOptions.IgnoreCase)) return true;

        // 3) No code AND description is just a number → "amount-only" row.
        if (string.IsNullOrEmpty(code))
        {
            var descrNoSpaces = descr.Replace(" ", "");
            if (descrNoSpaces.Length > 0 && descrNoSpaces.Length < 20
                && Regex.IsMatch(descrNoSpaces, @"^[\d.,]+(EUR)?$", RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    private sealed record DlMeta(
        string? DlRef, string? Akcia, string? Prevzal, string? Pozn,
        DateTime? Date, decimal? SubExclVat, decimal? SubVat,
        decimal DefaultVatRate, bool HasReverseCharge);

    private DlMeta ParseDeliveryListMeta(string body)
    {
        // The first "DL-XXX" token after "za dodací list" is the DL ref.
        var dlRefMatch = Regex.Match(body, @"za\s+dodací\s+list\s+([A-Z0-9\-]+)", RegexOptions.IgnoreCase);
        var dlRef = dlRefMatch.Success ? dlRefMatch.Groups[1].Value : null;

        var akcia   = AkciaRx.Match(body).Groups[1].Value.OrNull()?.Trim().TrimEnd('|').Trim();
        // "akcia: ." sentinel → null
        if (akcia == "." || akcia == "-") akcia = null;

        var prevzal = PrevzalRx.Match(body).Groups[1].Value.OrNull()?.Trim().TrimEnd('|').Trim();
        var pozn    = PoznDlRx.Match(body).Groups[1].Value.OrNull()?.Trim();
        var date    = ParseSkDate(DlDateRx.Match(body).Groups[1].Value);

        decimal? subExclVat = null;
        decimal? subVat     = null;
        decimal defaultVatRate = 23m;
        bool hasReverseCharge = false;

        // Subtotal pattern: "základ DPH 23% 751,25 EUR | DPH 23% 172,79 EUR"
        // Multiple rates can appear on the same line; sum them.
        foreach (Match m in SubtotalRx.Matches(body))
        {
            var rate    = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var baseAmt = SlovakNumberHelper.TryParse(m.Groups[2].Value) ?? 0m;
            var vatAmt  = SlovakNumberHelper.TryParse(m.Groups[3].Value) ?? 0m;
            subExclVat = (subExclVat ?? 0m) + baseAmt;
            subVat     = (subVat ?? 0m) + vatAmt;
            if (rate == 0m) hasReverseCharge = true;
            else defaultVatRate = rate;  // last non-zero rate wins; usually 23
        }

        return new DlMeta(dlRef, akcia, prevzal, pozn, date, subExclVat, subVat, defaultVatRate, hasReverseCharge);
    }

    // ─── Line-item mapper ──────────────────────────────────────────

    private static ParsedLine MapLineItem(
        DocumentAiEntity li,
        decimal defaultVat,
        bool segmentHasReverseCharge,
        string text,
        int rowStart,
        int rowEnd)
    {
        // Document AI line_item nested property keys we expect:
        //   line_item/description, line_item/quantity, line_item/unit_price,
        //   line_item/amount, line_item/product_code
        string? descr      = Prop(li, "description") ?? li.MentionText;
        string? supplierCd = Prop(li, "product_code");
        decimal? quantity  = SlovakNumberHelper.TryParse(Prop(li, "quantity"));
        decimal? aiUnitPrice = SlovakNumberHelper.TryParse(Prop(li, "unit_price"));
        decimal? aiAmount  = SlovakNumberHelper.TryParse(Prop(li, "amount"));
        string? unit       = Prop(li, "unit");

        // Detect reverse-charge: line starts with "**" prefix or its description
        // contains the asterisks marker. Also propagate segment-level info.
        bool isReverse = (descr?.Contains("**") ?? false)
                         || (supplierCd?.Contains("**") ?? false)
                         || segmentHasReverseCharge && (descr?.Contains("KR KH") ?? false);

        var vatRate = isReverse ? 0m : defaultVat;

        bool isService = descr != null &&
            (descr.Contains("Prenájom", StringComparison.OrdinalIgnoreCase)
             || descr.Contains("Zľava", StringComparison.OrdinalIgnoreCase));

        // Try the text-based 5-column extractor first — it's far more reliable
        // than Document AI's per-row column mapping on SK construction invoices.
        // Falls back to Document AI's values when the regex doesn't match.
        decimal? listPrice = null;
        decimal? discountPercent = null;
        decimal? unitPriceExcl = aiUnitPrice;
        decimal? unitPriceIncl = null;
        decimal? lineTotal = aiAmount;

        var rowPrices = ExtractRowPrices(text, rowStart, rowEnd);
        if (rowPrices.HasValue)
        {
            listPrice       = rowPrices.Value.list;
            discountPercent = rowPrices.Value.discount;
            unitPriceExcl   = rowPrices.Value.postExcl;
            unitPriceIncl   = rowPrices.Value.postIncl;
            lineTotal       = rowPrices.Value.total;
        }

        return new ParsedLine(
            SupplierItemCode: supplierCd?.Trim().TrimStart('*').Trim(),
            Description: DedupRepeatedDescription((descr ?? "").Trim().TrimStart('*').Trim()),
            Quantity: quantity,
            Unit: unit,
            ListPriceExclVat: listPrice,
            DiscountPercent: discountPercent,
            UnitPriceExclVat: unitPriceExcl,
            UnitPriceInclVat: unitPriceIncl,
            LineTotalExclVat: lineTotal,
            VatRate: vatRate,
            IsReverseCharge: isReverse,
            IsService: isService,
            Confidence: li.Confidence);
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private static DocumentAiEntity? FindEntity(IReadOnlyList<DocumentAiEntity> entities, string type)
        => entities.FirstOrDefault(e => e.Type == type);

    private static string? Prop(DocumentAiEntity li, string suffix)
        => li.Properties.FirstOrDefault(p => p.Type.EndsWith("/" + suffix, StringComparison.Ordinal))?.MentionText;

    private static DateTime? ParseSkDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // SK invoices use d.M.yyyy or dd.MM.yyyy.
        var formats = new[] { "d.M.yyyy", "dd.MM.yyyy", "d.MM.yyyy", "dd.M.yyyy" };
        if (DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    private static DateTime? ParseEntityDate(IReadOnlyList<DocumentAiEntity> entities, string type)
    {
        var raw = FindEntity(entities, type)?.NormalizedValue ?? FindEntity(entities, type)?.MentionText;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Document AI returns dates in ISO (YYYY-MM-DD) when normalized.
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
            return d;
        return ParseSkDate(raw);
    }

    /// <summary>
    /// Find the next occurrence of a line_item entity's identifying token in
    /// the document text. When the product_code repeats across delivery
    /// lists (e.g. PN10010 appears 4× on the DEK invoice, SECA lata
    /// 3020200254 appears 2×), we disambiguate by ALSO matching the
    /// description prefix near the code. Returns -1 when no match is found
    /// at or after the cursor.
    /// </summary>
    private static int FindNextOffset(DocumentAiEntity e, string text, int fromIndex)
    {
        if (string.IsNullOrEmpty(text) || fromIndex >= text.Length) return -1;

        var code = e.Properties.FirstOrDefault(p => p.Type.EndsWith("/product_code", StringComparison.Ordinal))?.MentionText?.Trim();
        var descrRaw = e.Properties.FirstOrDefault(p => p.Type.EndsWith("/description", StringComparison.Ordinal))?.MentionText;
        // Use a short description marker — the OCR'd description might be
        // duplicated (PS01090 case) so take the first 12 chars only.
        var descrMarker = (descrRaw ?? "").Trim();
        if (descrMarker.Length > 12) descrMarker = descrMarker[..12];

        // 1) Combined: find a product_code whose description appears within
        //    ~200 chars after it. This is the strongest signal — disambiguates
        //    duplicate product codes (PN10010 across 4 delivery lists).
        if (!string.IsNullOrEmpty(code) && descrMarker.Length >= 5)
        {
            int searchFrom = fromIndex;
            while (searchFrom < text.Length)
            {
                var codeIdx = text.IndexOf(code, searchFrom, StringComparison.Ordinal);
                if (codeIdx < 0) break;
                var windowEnd = Math.Min(text.Length, codeIdx + code.Length + 250);
                var window = text.Substring(codeIdx, windowEnd - codeIdx);
                if (window.Contains(descrMarker, StringComparison.Ordinal))
                    return codeIdx;
                searchFrom = codeIdx + 1;
            }
        }

        // 2) Plain product_code search.
        if (!string.IsNullOrEmpty(code))
        {
            var idx = text.IndexOf(code, fromIndex, StringComparison.Ordinal);
            if (idx >= 0) return idx;
        }

        // 3) Description prefix.
        if (descrMarker.Length >= 5)
        {
            var idx = text.IndexOf(descrMarker, fromIndex, StringComparison.Ordinal);
            if (idx >= 0) return idx;
        }

        return -1;
    }

    /// <summary>
    /// Document AI occasionally returns a line_item's description with the
    /// text repeating itself (the PS01090 row on the DEK invoice came back
    /// as "Zľava z prenájmu - bonus Požičovňa Zľava z prenájmu - bonus
    /// Požičovňa"). Detect by looking for the first 12 chars reappearing
    /// later and truncate at that point.
    /// </summary>
    private static string DedupRepeatedDescription(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        var t = s.Trim();
        if (t.Length < 24) return t;
        var marker = t[..Math.Min(12, t.Length / 2)];
        if (marker.Length < 5) return t;
        var second = t.IndexOf(marker, marker.Length, StringComparison.Ordinal);
        if (second > 0 && second < t.Length)
            return t[..second].TrimEnd();
        return t;
    }
}

internal static class StringExtensions
{
    public static string? OrNull(this string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;
}
