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

    // Per-rate VAT summary on Pohoda/Money-style layouts (A-Z STAV):
    //   "Základná sadzba DPH 23%   23   249,12   57,30   306,42EUR"
    // It carries base, VAT and incl-total — the only totals such invoices
    // print with an EUR anchor. Photo-scan OCR reflows the block (label
    // first, values drifting several lines lower), so the amounts are read
    // from a bounded window after the label rather than a fixed row shape:
    // base = 1st money token, VAT = 2nd, incl-total = 1st money token with
    // an EUR/€ suffix. The bare rate column ("23") has no decimals and is
    // skipped automatically.
    private static readonly Regex VatSummaryLabelRx = new(
        @"základná\s+sadzba\s+DPH\s+\d{1,2}\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MoneyTokenRx = new(
        @"(?<![\d,])(-?\d{1,4}(?:[  ]\d{3})*,\d{2})(\s*(?:EUR|€))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (decimal? Base, decimal? Vat, decimal? Incl) ExtractVatSummaryTotals(string text)
    {
        var label = VatSummaryLabelRx.Match(text);
        if (!label.Success) return (null, null, null);
        var window = text[label.Index..Math.Min(text.Length, label.Index + label.Length + 400)];

        var plain = new List<decimal>();
        decimal? incl = null;
        foreach (Match m in MoneyTokenRx.Matches(window))
        {
            var v = SlovakNumberHelper.TryParse(m.Groups[1].Value);
            if (!v.HasValue) continue;
            if (m.Groups[2].Success && m.Groups[2].Length > 0) incl ??= v;
            else plain.Add(v.Value);
        }

        // The reflowed window repeats values in arbitrary order (one scan of
        // the A-Z STAV photo yielded base twice before the VAT), so blind
        // first/second assignment is unsafe. When the EUR-anchored incl-total
        // is known, pick the first pair that satisfies base + VAT = incl;
        // failing that, derive VAT from the first token.
        decimal? bas = null, vat = null;
        if (incl is { } total)
        {
            for (var i = 0; i < plain.Count && bas is null; i++)
                for (var j = i + 1; j < plain.Count; j++)
                    if (Math.Abs(plain[i] + plain[j] - total) <= 0.02m)
                    {
                        bas = plain[i];
                        vat = plain[j];
                        break;
                    }
            if (bas is null && plain.Count > 0 && total >= plain[0])
            {
                bas = plain[0];
                vat = total - plain[0];
            }
        }
        else if (plain.Count >= 2)
        {
            bas = plain[0];
            vat = plain[1];
        }
        else if (plain.Count == 1)
        {
            bas = plain[0];
        }
        return (bas, vat, incl);
    }

    // ─── Cash-receipt (pokladničný blok) patterns ──────────────────
    // Receipts (HORNBACH / MPL / PRESPOR reference scans) differ from
    // invoices: the paid total is labelled with EUR BEFORE the value
    // ("NA ÚHRADU EUR 15,65", "Spolu v EUR 38.75"), decimals may use a dot,
    // the VAT recap is a per-class row ("A 23%" / "C 23%") whose values may
    // reflow, the document number is a č. bloku / číslo účtenky / č.d., and
    // the date is "dňa: 25.05.2026" or dashed "03-06-2026".
    // Between the label and the value there may be a short run of OCR junk
    // (stamp bleed-through: "Spolu v EUR dego 59.10") — anything but digits,
    // bounded, so the value can never be skipped over.
    private static readonly Regex ReceiptInclRx = new(
        @"(?:na\s+úhradu|spolu\s+v)\s+(?:EUR|€)[^\d\-]{0,20}(-?\d{1,4}(?:[  ]\d{3})*[.,]\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReceiptRecapAnchorRx = new(
        @"(?:K\s+DPH|Sadzba\s*:)[\s\S]{0,120}?[A-Z]\s?\d{1,2}\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Dot OR comma decimals; a trailing [.,]digit means it's a date/quantity
    // fragment ("04.06.2026", "1.0000"), not money.
    private static readonly Regex ReceiptMoneyTokenRx = new(
        @"(?<![\d.,])(-?\d{1,4}(?:[  ]\d{3})*[.,]\d{2})(?![.,]?\d)(?!\s*%)",
        RegexOptions.Compiled);
    private static readonly Regex ReceiptNumberRx = new(
        @"(?:číslo\s+účtenky|č\.?\s*bloku|č\.?\s*d\.?)\s*:?\s*\n?\s*(\d{3,16})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Reflow can wedge other header fields between the "č.d.:" label and its
    // value (PRESPOR: "č.d.:\nIČO: 31340326\n145 dňa: 04.06.2026") — the
    // document number then still sits directly before "dňa".
    private static readonly Regex ReceiptNumberBeforeDnaRx = new(
        @"(?<!\d)(\d{3,16})\s+dňa\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReceiptDateRx = new(
        @"dňa\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReceiptDashDateRx = new(
        @"(?<!\d)(\d{2})-(\d{2})-(\d{4})(?!\d)",
        RegexOptions.Compiled);
    // A receipt item row: "1.00 x 30.56 C 30.56" (qty × unit [tax class] total).
    private static readonly Regex ReceiptItemRowRx = new(
        @"(?<qty>\d{1,4}[.,]\d{1,4})\s*[xX×]\s*(?<unit>\d{1,4}[.,]\d{2})[\s.]*(?:[A-Z]{1,3}[\s.]*)?(?<total>\d{1,4}[.,]\d{2})",
        RegexOptions.Compiled);
    // A bare PLU / article number on its own line.
    private static readonly Regex ReceiptPluLineRx = new(
        @"^\s*(\d{4,9})\s*$",
        RegexOptions.Compiled);
    // "ZĽAVA 10%" — OCR variants drop the mäkčeň or even the Z ("LAVA 10%"),
    // or wedge punctuation before the value ("ZLAVA, 10%" — PRESPOR phone
    // scan). The lookbehind keeps it from firing inside "Bratislava".
    private static readonly Regex ReceiptDiscountRx = new(
        @"(?<!\p{L})Z?[LĽ]AVA\s*[,.:]?\s*(\d{1,2})\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // The head of a register row without its total ("1.00 × 28.55") — photo
    // reflow displaces the total; it gets re-paired by value instead.
    private static readonly Regex ReceiptItemPartialRx = new(
        @"(?<qty>\d{1,4}[.,]\d{1,4})\s*[xX×]\s*(?<unit>\d{1,4}[.,]\d{2})",
        RegexOptions.Compiled);
    // eKasa fiscal-register code ("KP: 8882020…" / "KPEKK: 8882020…") — the
    // definitive cash-receipt marker; invoices never carry one.
    private static readonly Regex ReceiptKpMarkerRx = new(
        @"\bKP(?:EKK)?\s*:\s*\d{10,}",
        RegexOptions.Compiled);
    // HORNBACH-format receipts print each item's NET on its own line
    // ("Základ 2,60"). Case-sensitive and colon-less on purpose: the recap
    // header prints "Základ:" (colon → no match) and DEK invoices print
    // lowercase "základ DPH".
    private static readonly Regex ReceiptZakladRx = new(
        @"Základ\s+(-?\d{1,4},\d{2})",
        RegexOptions.Compiled);
    // "107H Sifón pračkový 1 Kus" / "Ružica plastová dele 1 kus" — item name
    // and quantity on one line.
    private static readonly Regex ReceiptKusNameRx = new(
        @"([^\n]+?)\s+(\d{1,3})\s*[Kk]us\b",
        RegexOptions.Compiled);
    private static readonly Regex ReceiptEanRx = new(
        @"EAN\s*:?\s*(\d{8,14})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Name cleanup: an OCR-glued EAN tail ("DVIERKA … D 1c. art./EAN 859…")
    // and a leading money+class fragment from the previous item's column.
    private static readonly Regex ReceiptNameEanTailRx = new(
        @"\s*\d{0,3}\s*[cćč]?\.?\s*art\.?\s*/?\s*EAN.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReceiptNameMoneyPrefixRx = new(
        @"^\s*-?\d{1,4}(?:,\d{2})?\s+[A-Z]\s+(?=\p{L})",
        RegexOptions.Compiled);

    /// <summary>
    /// The handwritten site note on top of a receipt scan ("2 KALISOVÁ").
    /// Taken from the FIRST non-empty text line only, and only when it's a
    /// real word (≥5 letters) that isn't part of the supplier's own name —
    /// logo fragments ("HORNBA", "Ampl") are filtered by those two rules.
    /// </summary>
    private static string? ExtractReceiptTopNote(string text, string? supplierName)
    {
        var firstLine = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (firstLine is null) return null;

        var core = Regex.Match(firstLine, @"\p{L}[\p{L} ]*\p{L}").Value.Trim();
        if (core.Count(char.IsLetter) < 5) return null;
        if (Regex.IsMatch(firstLine, @"s\.\s*r\.\s*o|spol\.|a\.\s*s\.", RegexOptions.IgnoreCase)) return null;
        var supplierNorm = (supplierName ?? "").ToLowerInvariant();
        if (supplierNorm.Length > 0 && supplierNorm.Contains(core.ToLowerInvariant())) return null;
        return core;
    }

    // Standard Slovak item-table column headers — skipped when walking the
    // text above a line's anchor in search of the item name.
    private static readonly Regex TableHeaderLineRx = new(
        @"^\s*(tovar|množstvo|popis|kód|číslo|mj|mn\.?|jedn\w*\.?.*|cena\b.*|bez\s+dph.*|s\s+dph.*|dph\b.*|daň\s*%?.*|spolu\b.*|celkom\b.*|základ\b.*|sadzba\b.*|zľava\b.*)\s*:?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The item name printed directly above a line's anchor: walk backward,
    /// skip table-header words and number-only lines, collect up to three
    /// consecutive text lines and stop once the block ends. Null when
    /// nothing name-like is found nearby.
    /// </summary>
    private static string? ExtractNameAboveAnchor(string text, int anchor)
    {
        if (anchor <= 0 || anchor > text.Length) return null;
        var lines = text[..anchor].Split('\n');
        var collected = new List<string>();
        for (var k = lines.Length - 1; k >= 0 && k >= lines.Length - 12 && collected.Count < 3; k--)
        {
            var cand = lines[k].Trim();
            if (cand.Length == 0) continue;
            var isHeader = TableHeaderLineRx.IsMatch(cand);
            var isNumeric = cand.Count(char.IsLetter) < 3;
            if (isHeader || isNumeric)
            {
                if (collected.Count > 0) break;   // name block ended
                continue;
            }
            collected.Insert(0, cand);
        }
        if (collected.Count == 0) return null;
        var name = Regex.Replace(string.Join(" ", collected), @"\s+", " ").Trim();
        return name.Length >= 6 ? name : null;
    }

    private static bool IsReceiptNameLine(string line)
    {
        var t = line.Trim();
        if (t.Count(char.IsLetter) < 3) return false;
        if (Regex.IsMatch(t, @"^z?lava\b", RegexOptions.IgnoreCase)) return false;    // ZLAVA 10% (OCR may drop the Z)
        if (t.Contains("art./EAN", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.StartsWith("Základ", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Rebuild a receipt's items from its text when Document AI's line set
    /// doesn't add up (PRESPOR 145 returned one real item + a recap echo
    /// instead of the two printed items). Anchors on the "qty × unit [class]
    /// total" rows; the item name is the nearest preceding text line and the
    /// PLU the nearest preceding bare number. Rows are built GROSS — the
    /// gross→net conversion that follows turns them into the printed base.
    /// Fires only when the text rows reconcile with base+VAT while the
    /// existing lines reconcile with neither the base nor the gross.
    /// </summary>
    private static IReadOnlyList<ParsedDeliveryList> RecoverReceiptLinesFromText(IReadOnlyList<ParsedDeliveryList> dls, string text)
    {
        if (dls.Count != 1) return dls;
        var dl = dls[0];
        if (dl.DeliveryNoteRef != null) return dls;
        if (dl.SubtotalExclVat is not { } bas || bas <= 0m) return dls;
        if (dl.SubtotalVat is not { } vat || vat <= 0m) return dls;

        var gross = bas + vat;
        var lineSum = dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m);
        if (Math.Abs(lineSum - bas) <= 0.02m || Math.Abs(lineSum - gross) <= 0.06m)
            return dls;   // lines already usable (net or gross) → other fixes handle them

        var textLines = text.Split('\n');
        // Complete rows, matched over the WHOLE text (the total may sit on
        // the next line — MPL photo).
        var rows = new List<(int LineIdx, decimal? Qty, decimal Unit, decimal Total)>();
        var spans = new List<(int Start, int End)>();
        foreach (Match m in ReceiptItemRowRx.Matches(text))
        {
            var qty = SlovakNumberHelper.TryParse(m.Groups["qty"].Value);
            var unit = SlovakNumberHelper.TryParse(m.Groups["unit"].Value);
            var total = SlovakNumberHelper.TryParse(m.Groups["total"].Value);
            if (unit is not { } u || total is not { } t) continue;
            // OCR mangles quantities ("1.00" → "9.00") — trust the quantity
            // only when qty × unit equals the row total; otherwise derive it
            // from total ÷ unit when that lands on a whole number.
            if (qty is not { } q || Math.Abs(q * u - t) > 0.02m)
            {
                qty = null;
                if (u > 0m)
                {
                    var derived = t / u;
                    var rounded = Math.Round(derived, 0, MidpointRounding.AwayFromZero);
                    if (rounded >= 1m && Math.Abs(derived - rounded) <= 0.02m) qty = rounded;
                }
            }
            rows.Add((LineIndexOf(text, m.Index), qty, u, t));
            spans.Add((m.Index, m.Index + m.Length));
        }
        // Orphaned row heads: reflow severed the total from its "qty × unit"
        // ("ZLAVA, 10% 1.00 × 28.55 с │ 7120914 │ x C │ 28.55" — PRESPOR
        // phone scan; a Cyrillic с blocks the class group). Pair each head
        // with the first money token in the next 160 chars that equals
        // qty × unit — value-confirmed, so junk between can't mislead.
        foreach (Match p in ReceiptItemPartialRx.Matches(text))
        {
            if (spans.Any(s => p.Index >= s.Start && p.Index < s.End)) continue;
            var qty = SlovakNumberHelper.TryParse(p.Groups["qty"].Value);
            var unit = SlovakNumberHelper.TryParse(p.Groups["unit"].Value);
            if (qty is not { } q || unit is not { } u || q < 1m || q > 999m || u <= 0m) continue;
            var expected = Math.Round(q * u, 2, MidpointRounding.AwayFromZero);
            var end = p.Index + p.Length;
            var window = text[end..Math.Min(text.Length, end + 160)];
            var hit = ReceiptMoneyTokenRx.Matches(window)
                .Select(w => SlovakNumberHelper.TryParse(w.Groups[1].Value))
                .FirstOrDefault(v => v.HasValue && Math.Abs(v.Value - expected) <= 0.01m);
            if (hit is not { } t) continue;
            rows.Add((LineIndexOf(text, p.Index), q, u, t));
        }
        rows = rows.OrderBy(r => r.LineIdx).ToList();
        // No "×" register rows at all → HORNBACH layout (per-item "Základ"
        // net lines instead).
        if (rows.Count == 0) return RecoverReceiptZakladLines(dls, dl, text, bas, vat);
        if (Math.Abs(rows.Sum(r => r.Total) - gross) > 0.06m) return dls;

        var rate = Math.Round(vat / bas * 100m, 0, MidpointRounding.AwayFromZero);
        var lines = new List<ParsedLine>(rows.Count);
        var prevRowIdx = -1;
        foreach (var r in rows)
        {
            // PLU: nearest bare number above the row. Name: the candidate
            // line with the HIGHEST uppercase ratio within the block above —
            // receipts print item names in caps-heavy type, while stamp
            // bleed-through and variant-code lines OCR as lowercase noise
            // (PRESPOR 145: "MONT-FSBH06v) jail yndunes…" sits nearer than
            // the real "PS.FSB6 MONDRILLO BLACK…").
            string? name = null;
            string? code = null;
            var bestRatio = -1.0;
            var bestIdx = -1;
            // Zľava: printed above the row or OCR-merged into the row itself.
            decimal? discount = null;
            var dOwn = ReceiptDiscountRx.Match(textLines[r.LineIdx]);
            if (dOwn.Success) discount = SlovakNumberHelper.TryParse(dOwn.Groups[1].Value);

            // Never walk past the previous item's row — its name/PLU/zľava
            // belong to it.
            for (var k = r.LineIdx - 1; k > prevRowIdx && k >= r.LineIdx - 7; k--)
            {
                if (discount == null)
                {
                    var dm = ReceiptDiscountRx.Match(textLines[k]);
                    if (dm.Success) discount = SlovakNumberHelper.TryParse(dm.Groups[1].Value);
                }
                var cand = textLines[k].Trim();
                if (code == null)
                {
                    var plu = ReceiptPluLineRx.Match(cand);
                    if (plu.Success) { code = plu.Groups[1].Value; continue; }
                }
                if (!IsReceiptNameLine(cand)) continue;
                var letters = cand.Count(char.IsLetter);
                var ratio = (double)cand.Count(char.IsUpper) / letters;
                if (ratio > bestRatio + 0.15)   // clear winner only — nearest wins ties
                {
                    bestRatio = ratio;
                    bestIdx = k;
                    name = cand;
                }
            }
            // Wrapped name: a short lowercase continuation on the next line
            // ("…kor.za s" / "ucha").
            if (bestIdx >= 0 && bestIdx + 1 < r.LineIdx)
            {
                var cont = textLines[bestIdx + 1].Trim();
                if (cont.Length is > 0 and <= 10
                    && cont.Count(char.IsLetter) >= 3
                    && !cont.Any(char.IsUpper)
                    && !cont.Any(char.IsDigit))
                    name = $"{name} {cont}";
            }
            lines.Add(new ParsedLine(
                SupplierItemCode: code,
                Description: name ?? "",
                Quantity: r.Qty,
                Unit: "ks",
                ListPriceExclVat: null,
                DiscountPercent: discount,
                UnitPriceExclVat: null,
                UnitPriceInclVat: r.Unit,
                LineTotalExclVat: r.Total,   // gross here — net conversion follows
                VatRate: rate,
                IsReverseCharge: false,
                IsService: false,
                Confidence: 0.4f));
            prevRowIdx = r.LineIdx;
        }
        return new List<ParsedDeliveryList> { dl with { Lines = lines } };
    }

    /// <summary>
    /// HORNBACH-format recovery: no "qty × unit … total" register rows —
    /// every item instead prints its net on a "Základ 2,60" line, its name
    /// and quantity on a "… 1 Kus" line, and its EAN in between (receipt
    /// 815 photo: Document AI put the GROSS into quantity and returned the
    /// recap echoes as items). The per-item nets must sum to the recap base
    /// (±0,06 — per-item rounding); the residual cent lands on the largest
    /// line so the list is cent-exact.
    /// </summary>
    private static IReadOnlyList<ParsedDeliveryList> RecoverReceiptZakladLines(
        IReadOnlyList<ParsedDeliveryList> dls, ParsedDeliveryList dl, string text, decimal bas, decimal vat)
    {
        var zaklady = ReceiptZakladRx.Matches(text).ToList();
        if (zaklady.Count == 0) return dls;
        var nets = zaklady.Select(m => SlovakNumberHelper.TryParse(m.Groups[1].Value)).ToList();
        if (nets.Any(n => n is null or <= 0m)) return dls;
        if (Math.Abs(nets.Sum(n => n!.Value) - bas) > 0.06m) return dls;

        var rate = Math.Round(vat / bas * 100m, 0, MidpointRounding.AwayFromZero);
        var lines = new List<ParsedLine>(zaklady.Count);
        for (var i = 0; i < zaklady.Count; i++)
        {
            // Everything between the previous item's "Základ" line and this
            // one belongs to this item — reflow-proof (the gross column
            // drifts freely, even mangled: "40 A" for 0,40).
            var segStart = i == 0 ? 0 : zaklady[i - 1].Index + zaklady[i - 1].Length;
            var seg = text[segStart..zaklady[i].Index];

            string? name = null;
            var qty = 1m;
            var kus = ReceiptKusNameRx.Matches(seg).LastOrDefault();
            if (kus != null)
            {
                name = CleanReceiptItemName(kus.Groups[1].Value);
                var q = SlovakNumberHelper.TryParse(kus.Groups[2].Value);
                if (q is { } qq && qq >= 1m) qty = qq;
            }
            else
            {
                // "Kus" lost to OCR — nearest line above that still reads
                // like a name after cleanup.
                name = seg.Split('\n')
                    .Select(CleanReceiptItemName)
                    .LastOrDefault(l => l.Length >= 4 && l.Count(char.IsLetter) >= 4);
            }
            var ean = ReceiptEanRx.Matches(seg).LastOrDefault();

            var net = nets[i]!.Value;
            lines.Add(new ParsedLine(
                SupplierItemCode: ean?.Groups[1].Value,
                Description: name ?? "",
                Quantity: qty,
                Unit: "ks",
                ListPriceExclVat: null,
                DiscountPercent: null,
                UnitPriceExclVat: Math.Round(net / qty, 4, MidpointRounding.AwayFromZero),
                UnitPriceInclVat: null,
                LineTotalExclVat: net,   // already the printed net — no gross conversion needed
                VatRate: rate,
                IsReverseCharge: false,
                IsService: false,
                Confidence: 0.4f));
        }

        var residual = Math.Round(bas - lines.Sum(l => l.LineTotalExclVat ?? 0m), 2, MidpointRounding.AwayFromZero);
        if (residual != 0m)
        {
            var idx = 0;
            for (var i = 1; i < lines.Count; i++)
                if ((lines[i].LineTotalExclVat ?? 0m) > (lines[idx].LineTotalExclVat ?? 0m)) idx = i;
            var adj = (lines[idx].LineTotalExclVat ?? 0m) + residual;
            var adjQty = lines[idx].Quantity ?? 1m;
            lines[idx] = lines[idx] with
            {
                LineTotalExclVat = adj,
                UnitPriceExclVat = adjQty != 0m
                    ? Math.Round(adj / adjQty, 4, MidpointRounding.AwayFromZero)
                    : lines[idx].UnitPriceExclVat
            };
        }
        return new List<ParsedDeliveryList> { dl with { Lines = lines } };
    }

    private static string CleanReceiptItemName(string raw)
    {
        var s = raw.Trim();
        s = ReceiptNameEanTailRx.Replace(s, "");
        s = ReceiptNameMoneyPrefixRx.Replace(s, "");
        return s.Trim();
    }

    private static int LineIndexOf(string text, int charIndex)
    {
        var n = 0;
        for (var i = 0; i < charIndex && i < text.Length; i++)
            if (text[i] == '\n') n++;
        return n;
    }

    /// <summary>
    /// Base and VAT from a receipt's per-class recap row ("C 23% 31.52 7.25
    /// 38.77" — possibly reflowed). When the paid total is known, the first
    /// token pair summing to it (±0,06 for cash rounding) wins; otherwise the
    /// first two tokens (receipts print base before VAT).
    /// </summary>
    private static (decimal? Base, decimal? Vat) ExtractReceiptRecapTotals(string text, decimal? incl)
    {
        var anchor = ReceiptRecapAnchorRx.Match(text);
        if (!anchor.Success) return (null, null);
        var end = anchor.Index + anchor.Length;
        var window = text[end..Math.Min(text.Length, end + 200)];
        var tokens = ReceiptMoneyTokenRx.Matches(window)
            .Select(m => SlovakNumberHelper.TryParse(m.Groups[1].Value))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
        if (tokens.Count < 2) return (null, null);
        if (incl is { } total)
        {
            for (var i = 0; i < tokens.Count; i++)
                for (var j = i + 1; j < tokens.Count; j++)
                    if (Math.Abs(tokens[i] + tokens[j] - total) <= 0.06m)
                        return (tokens[i], tokens[j]);
        }
        return (tokens[0], tokens[1]);
    }

    // Bare-label variants ("Vyhotovenie:" / "Dodanie:" / "Splatnosť:") are the
    // BAU-ARTICEL layout — no "dátum" prefix.
    private static readonly Regex IssueDateRx    = new(@"(?:dátum\s+(?:vyhotovenia|vydania|vystavenia)|vyhotovenie)\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // For scrambled headers where labels and values OCR as separate runs
    // (A-Z STAV photo layout) — paired by order in ParseHeader when the
    // issue date's own regex didn't fire.
    private static readonly Regex DateLabelRx = new(
        @"dátum\s+(?<kind>vyhotovenia|vydania|vystavenia|dodania|splatnosti)|(?<kind>vyhotovenie|dodanie|splatnosť)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DateTokenRx = new(
        @"(?<!\d)\d{1,2}\.\d{1,2}\.\d{4}(?!\d)",
        RegexOptions.Compiled);
    private static readonly Regex DueDateRx      = new(@"(?:dátum\s+splatnosti|splatnosť)\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderDelDateRx = new(@"(?:dátum\s+dodania|dodanie)\s*:?\s*(\d{1,2}\.\d{1,2}\.\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Grand totals on the last page. DEK prints "cena bez DPH … EUR".
    // Single-table suppliers (HEKTRANS / Doklado layout) print a summary box
    // where "Základ DPH" is followed by the "Výška DPH" / "Celkom" labels
    // BEFORE the values, so those labels are optionally skipped. The per-DL
    // "základ DPH <rate> % … EUR" subtotal lines cannot match here because
    // the "%" sign blocks the amount capture.
    // "základ dane: 43,61 EUR" is the labelled tax base on §69 reverse-charge
    // invoices (KOVOUNI-BA layout) — on those the excl total equals the incl
    // total and no "cena bez DPH … EUR" form exists.
    private static readonly Regex TotalExclVatRx = new(@"(?:cena\s+bez\s+DPH|základ\s+dane|základ\s+DPH(?:\s*výška\s+DPH)?(?:\s*celkom)?)\s*:?\s*([\d\s.,]+?)\s*(?:EUR|€)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    private const string IcoHilti      = "31344445";   // Hilti Slovakia layout

    // ─── Own company (the odberateľ on every supplier document) ────
    // Document AI regularly returns the CUSTOMER's block as supplier_name /
    // the first IČO — we can never be our own supplier. Treated as noise so
    // the dodávateľ-block and head-of-document scans take over.
    private const string OwnCompanyIco = "47208368";
    private const string OwnCompanyIcDph = "SK2023830160";
    private static readonly Regex OwnCompanyNameRx = new(
        @"profistav",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A "quantity unit" token, e.g. "6,30 t" / "1 234,5 kg". Comma decimals
    // only, so a weighbridge "1.65 t" (dot) on an extra page is ignored.
    private static readonly Regex QtyUnitRx = new(
        @"(\d{1,3}(?:[\s ]\d{3})*,\d+)\s*(t|kg|ks|m3|m²|m2|m|l|bal|hod|km|bm|pal)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ─── DEK súhrnná faktúra line-total repair ─────────────────────
    // DEK = Stavebniny DEK. Document AI OCRs its multi-delivery-list summary
    // invoices inconsistently (row-contiguous vs column-grouped), which
    // mismatches per-line prices/totals. These re-derive each line's total
    // straight from the text, per delivery list, keeping Document AI's reliable
    // codes/descriptions/quantities. A DEK line is: <code> <desc> <qty><unit>
    // then 5 cells — cenník, zľava%, cena po zľave, cena po zľave s DPH, spolu.
    // "x" is the rental unit (Prenájom rows); the sign supports the negative
    // quantity on credit rows ("Zľava z prenájmu … -1,00 x") so the whole
    // token is recognized as a quantity and excluded from the money cells.
    private static readonly Regex DekQtyRx = new(
        @"(-?\d{1,4}(?:,\d+)?)\s+(ks|bal\.?|doska|vrece|pár|rol|kus|m2|m3|kg|t|l|bm|pal|sada|hod|cm|m|x)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // A money cell: a 2-decimal number that isn't a percentage (zľava) and
    // isn't an "… EUR" subtotal / list-total (those aren't per-line columns).
    // May be negative (credit rows). A trailing letter disqualifies the token —
    // that's a description fragment like "3,78m3/pal", not a column cell.
    private static readonly Regex DekPriceRx = new(
        @"(?<![\d,])(-?\d{1,4},\d{2})(?![\dA-Za-z])(?!\s*%)(?!\s*EUR)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // A DEK row's product code: 10-digit numeric (materials) or PN/PS+5
    // (rental / rental-credit). Used by the text-recovery path to count and
    // anchor rows when Document AI returned no usable line_item entities.
    private static readonly Regex DekRowCodeRx = new(
        @"(?<![\dA-Za-z])(\d{10}|P[NS]\d{5})(?![\dA-Za-z])",
        RegexOptions.Compiled);
    // End of row data for the CODE scan during recovery. Reflow can push a
    // row's code past its printed subtotal (FA 2600132372 / DL-100-26-014817),
    // so codes are scanned up to the invoice footer instead: the grand-total
    // block or the "upozornenie pre zákazníka" bullet list — the latter also
    // keeps "Aplik. dobropisy: <10-digit ref>" out of the row count.
    private static readonly Regex DekRowDataEndRx = new(
        @"cena\s+bez\s+DPH|upozornenie\s+pre\s+zákazníka",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Marks where a delivery list's line data ends — the first subtotal
    // ("základ DPH …") or the invoice grand-total block ("cena bez DPH").
    private static readonly Regex DekSegmentEndRx = new(
        @"(?:základ\s+DPH|cena\s+bez\s+DPH)",
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
        var deliveryLists = TryParseBySupplier(header, text, ocr.Entities)
                            ?? ParseDeliveryLists(text, ocr.Entities);

        // No "za dodací list" grouping (single-table suppliers like
        // HEKTRANS): the one synthetic segment has no printed "základ DPH
        // X% … | DPH X% …" subtotal, so carry the header summary-box totals
        // onto it instead of leaving them null. Runs BEFORE the subtotal-based
        // line repair below so single-table invoices get repaired too.
        if (deliveryLists.Count == 1 && deliveryLists[0].DeliveryNoteRef == null
            && !deliveryLists[0].SubtotalExclVat.HasValue
            && (header.TotalExclVat.HasValue || header.TotalVat.HasValue))
        {
            var single = deliveryLists[0] with
            {
                SubtotalExclVat = header.TotalExclVat,
                SubtotalVat = header.TotalVat
            };

            // Same zero-VAT rule as per-DL segments, for single-table
            // suppliers: printed excl == printed incl (VAT 0,00) means the
            // whole invoice is reverse-charge ("Prenesenie daňovej povinnosti
            // podľa §69" — e.g. KOVOUNI-BA processed rebar), so no line may
            // carry VAT. Without this the lines default to 23 % and the
            // reconciliation is off by exactly the phantom VAT.
            if (single.SubtotalVat == 0m && (single.SubtotalExclVat ?? 0m) > 0m)
                single = single with
                {
                    Lines = single.Lines
                        .Select(l => l with { VatRate = 0m, IsReverseCharge = true })
                        .ToList()
                };

            deliveryLists = new List<ParsedDeliveryList> { single };
        }

        // Receipts: when Document AI's line set reconciles with neither the
        // base nor the gross total, rebuild the items from the printed
        // "qty × unit … total" rows (PRESPOR 145 lost its second item).
        deliveryLists = RecoverReceiptLinesFromText(deliveryLists, text);

        // Receipts: the handwritten site note on top of the scan
        // ("2 KALISOVÁ" on PRESPOR 145) is the akcia — surface it so the
        // Pracovisko auto-match can assign the purchase to the right site.
        if (deliveryLists.Count == 1 && deliveryLists[0].DeliveryNoteRef == null
            && deliveryLists[0].AkciaName == null && ReceiptInclRx.IsMatch(text))
        {
            var note = ExtractReceiptTopNote(text, header.SupplierName);
            if (note != null)
                deliveryLists = new List<ParsedDeliveryList> { deliveryLists[0] with { AkciaName = note } };
        }

        // Receipts: Document AI often emits the VAT recap's grand total as an
        // extra quantity-less "line" (PRESPOR 145: a 59,11 row next to the
        // real 30,56 item). Drop such recap echoes BEFORE the arithmetic
        // repairs below — they double the sum.
        deliveryLists = deliveryLists
            .Select(dl =>
            {
                if (dl.Lines.Count < 2
                    || dl.SubtotalExclVat is not { } b || dl.SubtotalVat is not { } v || v <= 0m)
                    return dl;
                var kept = dl.Lines
                    .Where(l => (l.Quantity ?? 0m) != 0m
                                || l.LineTotalExclVat is not { } lt
                                || Math.Abs(lt - (b + v)) > 0.06m)
                    .ToList();
                return kept.Count == dl.Lines.Count || kept.Count == 0 ? dl : dl with { Lines = kept };
            })
            .ToList();

        // ── Post-correction ──────────────────────────────────────────
        // Document AI sometimes returns the same amount for visually-
        // similar adjacent rows (e.g. the PN10010 rental + PS01090 credit
        // pair on DL-100-26-015519 of the DEK invoice), or maps the wrong
        // column into a lone line's amount (KOVOUNI-BA photo scan). When
        // that breaks the arithmetic, infer the wrong row's total from
        // (subtotal − sum-of-other-rows). The printed subtotal is the
        // most reliable signal Document AI gave us.
        deliveryLists = InferDuplicateLineTotalsFromSubtotal(deliveryLists);

        // Cash receipts price their lines GROSS (incl. VAT) while the recap
        // gives the net base — when the lines sum to base+VAT, convert them
        // to net so materials are stored excl-VAT like everywhere else.
        deliveryLists = FixReceiptGrossLines(deliveryLists);

        // Receipts: Document AI shuffles the register-row columns
        // ("1.0000 x 38.77 C" → quantity 38.77, unit "C"). The printed rows
        // are authoritative — re-pair quantity/unit from the text.
        deliveryLists = FixReceiptLineQuantities(deliveryLists, text);

        // DEK súhrnná faktúra: re-derive per-line totals from the text (matched
        // per delivery list by its DL ref) to repair Document AI's scrambled
        // price/total mapping. Recognised by its delivery-list structure rather
        // than the supplier name (adopted from master) — Document AI sometimes
        // mislabels the buyer as the supplier on these. Safe for non-DEK
        // invoices: it only touches delivery lists whose DL ref is found and
        // whose arithmetic lines up.
        if (text.Contains("za dodací list", StringComparison.OrdinalIgnoreCase))
            deliveryLists = FixDekLineTotals(deliveryLists, text, header.InvoiceNumber);

        return new ParsedInvoice(header, deliveryLists);
    }

    /// <summary>
    /// For each DEK delivery list, re-extract the line totals from its text
    /// segment and overwrite them (and the unit price). Conservative: only
    /// touches a delivery list when its DL ref is found in the text AND the
    /// number of totals extracted equals the number of lines — otherwise the
    /// original (general-parser) result is kept untouched.
    /// </summary>
    private static IReadOnlyList<ParsedDeliveryList> FixDekLineTotals(IReadOnlyList<ParsedDeliveryList> dls, string text, string? invoiceNumber)
    {
        var result = new List<ParsedDeliveryList>(dls.Count);
        foreach (var dl in dls)
        {
            // Drop phantom lines — a footnote paragraph Document AI mistook for
            // a line has neither a product code nor a real quantity. (A real
            // line missing only its code still has qty > 0, so it's kept.)
            var lines = dl.Lines
                .Where(l => !string.IsNullOrEmpty(l.SupplierItemCode) || (l.Quantity ?? 0m) > 0m)
                .ToList();

            if (string.IsNullOrEmpty(dl.DeliveryNoteRef))
            {
                result.Add(lines.Count == dl.Lines.Count ? dl : dl with { Lines = lines });
                continue;
            }

            // Isolate this delivery list's segment: from just after its DL ref
            // up to the next "za dodací list".
            var idx = text.IndexOf(dl.DeliveryNoteRef, StringComparison.Ordinal);
            if (idx < 0) { result.Add(dl with { Lines = lines }); continue; }
            var afterRef = idx + dl.DeliveryNoteRef.Length;
            var nextIdx = text.IndexOf("za dodací list", afterRef, StringComparison.OrdinalIgnoreCase);
            var segFull = nextIdx > afterRef ? text[afterRef..nextIdx] : text[afterRef..];

            if (lines.Count == 0)
            {
                // Document AI returned no usable row for this delivery list.
                // Page-boundary rows are the usual victims (FA 2600132372 /
                // DL-100-26-014819: the row's cells were reflowed after the
                // printed subtotal and the entity never arrived) — the review
                // then under-counts by the whole delivery list. The text still
                // carries the full row(s), so rebuild them from the segment.
                var recovered = RecoverDekLinesFromText(segFull, dl.SubtotalExclVat, dl.SubtotalVat, invoiceNumber);
                result.Add(recovered.Count > 0
                    ? dl with { Lines = recovered }
                    : (lines.Count == dl.Lines.Count ? dl : dl with { Lines = lines }));
                continue;
            }

            // Cut at the first subtotal / grand-total marker for the per-line
            // total repair (the line numbers all precede it).
            var seg = segFull;
            var endM = DekSegmentEndRx.Match(seg);
            if (endM.Success) seg = seg[..endM.Index];

            var totals = ExtractDekLineTotals(seg, lines.Count);
            if (totals.Count != lines.Count)
            {
                // Phantom-filtered but totals not confidently re-derived — keep
                // the (filtered) lines as the general parser produced them.
                result.Add(dl with { Lines = lines });
                continue;
            }

            var newLines = new List<ParsedLine>(lines.Count);
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var total = totals[i];
                var qty = line.Quantity ?? 0m;
                decimal? unitPrice = qty != 0m
                    ? Math.Round(total / qty, 4, MidpointRounding.AwayFromZero)
                    : line.UnitPriceExclVat;
                newLines.Add(line with
                {
                    LineTotalExclVat = total,
                    UnitPriceExclVat = line.UnitPriceExclVat is null or 0m ? unitPrice : line.UnitPriceExclVat,
                    ListPriceExclVat = line.ListPriceExclVat is null or 0m ? unitPrice : line.ListPriceExclVat
                });
            }
            var newSubExcl = newLines.Sum(l => l.LineTotalExclVat ?? 0m);
            result.Add(dl with { Lines = newLines, SubtotalExclVat = dl.SubtotalExclVat ?? newSubExcl });
        }
        return result;
    }

    /// <summary>
    /// Extract a DEK delivery list's per-line totals (cena spolu bez DPH) from
    /// its segment text. Every line prints 4 money cells:
    ///   cenník · cena po zľave bez DPH · cena po zľave s DPH · spolu bez DPH.
    /// (The zľava% and the "… EUR" subtotals are excluded by the cell regex.)
    /// We expect 4·n cells. The layout is "line-grouped" (cells run line by
    /// line) when in every 4-cell group the 3rd cell — cena po zľave *s DPH* —
    /// equals the 2nd (0% VAT lines) or is at most ~25% above it (the VAT
    /// uplift); the total is then the 4th cell of each group. Otherwise Document
    /// AI grouped the table by *column* (cenník×n, poZľave×n, sDPH×n, spolu×n),
    /// and the totals are simply the last n cells. Returns empty (→ leave the
    /// general result) when the cell count doesn't line up.
    /// </summary>
    private static IReadOnlyList<decimal> ExtractDekLineTotals(string seg, int n)
    {
        if (n <= 0) return Array.Empty<decimal>();

        var qtySpans = DekQtyRx.Matches(seg).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        var vals = DekPriceRx.Matches(seg)
            .Select(m => (Val: SlovakNumberHelper.TryParse(m.Value), Pos: m.Index))
            .Where(x => x.Val.HasValue && !qtySpans.Any(q => x.Pos >= q.Index && x.Pos < q.End))
            .Select(x => x.Val!.Value)
            .ToList();

        if (vals.Count != 4 * n) return Array.Empty<decimal>();

        var lineGrouped = true;
        for (var k = 0; k < n && lineGrouped; k++)
        {
            var poZlave = vals[4 * k + 1];
            var sDph    = vals[4 * k + 2];
            if (sDph < poZlave - 0.01m || sDph > poZlave * 1.25m + 0.05m) lineGrouped = false;
        }

        var totals = new List<decimal>(n);
        if (lineGrouped)
            for (var k = 0; k < n; k++) totals.Add(vals[4 * k + 3]);
        else
            for (var i = 0; i < n; i++) totals.Add(vals[vals.Count - n + i]);
        return totals;
    }

    /// <summary>
    /// Last-resort DEK recovery for a delivery list where Document AI produced
    /// NO usable line_item entities. The segment text still carries every row
    /// (code, description, quantity and the 4 money cells — possibly reflowed
    /// past the printed subtotal), so rebuild the lines from text alone.
    /// Conservative: the result is accepted only when the rebuilt totals
    /// reconcile with the printed per-DL subtotal to the cent; otherwise an
    /// empty list is returned and the delivery list stays empty for manual
    /// review (the reconciliation banner already flags it).
    /// </summary>
    private static List<ParsedLine> RecoverDekLinesFromText(string seg, decimal? printedExcl, decimal? printedVat, string? invoiceNumber)
    {
        var none = new List<ParsedLine>();

        // Row codes: anywhere in the segment before the invoice footer —
        // reflow can push a page-bottom row's code past its printed subtotal,
        // so the subtotal is NOT a boundary here. The page-header repeat of
        // the invoice number (itself a 10-digit token) is excluded explicitly.
        var endM = DekRowDataEndRx.Match(seg);
        var codeRegion = endM.Success ? seg[..endM.Index] : seg;

        var codes = DekRowCodeRx.Matches(codeRegion)
            .Where(m => invoiceNumber == null || m.Groups[1].Value != invoiceNumber)
            .ToList();
        var n = codes.Count;
        if (n == 0) return none;

        // Money cells across the WHOLE segment, same cell model as
        // ExtractDekLineTotals: cenník · po zľave bez DPH · po zľave s DPH ·
        // spolu, 4 per row. Quantity tokens are excluded — but a quantity
        // match may not overlap a code token (the trailing digits of a code
        // followed by a unit-like word would otherwise swallow it).
        var codeSpans = DekRowCodeRx.Matches(seg).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        var qtyMatches = DekQtyRx.Matches(seg)
            .Where(q => !codeSpans.Any(c => c.Index < q.Index + q.Length && q.Index < c.End))
            .ToList();
        var qtySpans = qtyMatches.Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        var cells = DekPriceRx.Matches(seg)
            .Select(m => (Val: SlovakNumberHelper.TryParse(m.Groups[1].Value), Pos: m.Index))
            .Where(x => x.Val.HasValue && !qtySpans.Any(q => x.Pos >= q.Index && x.Pos < q.End))
            .Select(x => x.Val!.Value)
            .ToList();
        if (cells.Count != 4 * n) return none;

        // Same orientation test as ExtractDekLineTotals: line-grouped when
        // every 4-cell group's 3rd cell looks like "2nd cell + VAT uplift".
        var lineGrouped = true;
        for (var k = 0; k < n && lineGrouped; k++)
        {
            var poZlave = cells[4 * k + 1];
            var sDph    = cells[4 * k + 2];
            if (sDph < poZlave - 0.01m || sDph > poZlave * 1.25m + 0.05m) lineGrouped = false;
        }
        decimal CellAt(int col, int row) => lineGrouped ? cells[4 * row + col] : cells[col * n + row];

        var sum = 0m;
        for (var i = 0; i < n; i++) sum += CellAt(3, i);
        if (printedExcl.HasValue && Math.Abs(sum - printedExcl.Value) > 0.01m) return none;

        // VAT rates printed on this segment's subtotal line(s).
        var hasZeroRate = false;
        var sawStdRate  = false;
        var stdRate     = 23m;
        foreach (Match m in SubtotalRx.Matches(seg))
        {
            var rate = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (rate == 0m) hasZeroRate = true;
            else { stdRate = rate; sawStdRate = true; }
        }
        // The whole list is reverse-charge when its printed VAT is zero.
        var allZeroVat = printedVat == 0m || (hasZeroRate && !sawStdRate);

        var priceInRegion = DekPriceRx.Matches(codeRegion).ToList();
        var lines = new List<ParsedLine>(n);
        for (var i = 0; i < n; i++)
        {
            var code = codes[i].Groups[1].Value;

            // Description: text after this code, up to the next code or the
            // first subtotal/quantity/money token (line-grouped rows carry
            // their cells inline right after the description).
            var descStart = codes[i].Index + codes[i].Length;
            var descEnd = i + 1 < n ? codes[i + 1].Index : codeRegion.Length;
            var subInDesc = SubtotalRx.Match(codeRegion, descStart);
            if (subInDesc.Success && subInDesc.Index < descEnd) descEnd = subInDesc.Index;
            var qtyInDesc = qtyMatches.FirstOrDefault(m => m.Index >= descStart && m.Index < descEnd);
            if (qtyInDesc != null) descEnd = qtyInDesc.Index;
            var priceInDesc = priceInRegion.FirstOrDefault(m => m.Index >= descStart && m.Index < descEnd);
            if (priceInDesc != null) descEnd = priceInDesc.Index;
            var descr = Regex.Replace(codeRegion[descStart..descEnd], @"\s+", " ").Trim().TrimStart('*').Trim();
            // Reflow can leave nothing but stray amounts in the span — that's
            // not a description.
            if (Regex.IsMatch(descr, @"^[\d\s.,]*(EUR)?$")) descr = "";

            // Quantity: the LAST quantity token between this code and the
            // next (descriptions can embed qty-like text, e.g. "s násadou
            // 160 cm" — the real quantity is printed after the description).
            var rowStart = codes[i].Index;
            var rowEnd   = i + 1 < n ? codes[i + 1].Index : seg.Length;
            var qtyM = qtyMatches.LastOrDefault(m => m.Index >= rowStart && m.Index < rowEnd);
            var qty  = qtyM != null ? SlovakNumberHelper.TryParse(qtyM.Groups[1].Value) : null;

            var poZlave  = CellAt(1, i);
            var sDph     = CellAt(2, i);
            var total    = CellAt(3, i);
            var noUplift = Math.Abs(sDph - poZlave) <= 0.01m;
            var vatRate  = allZeroVat || (hasZeroRate && noUplift) ? 0m : stdRate;

            lines.Add(new ParsedLine(
                SupplierItemCode: code,
                Description: descr,
                Quantity: qty,
                Unit: qtyM?.Groups[2].Value,
                ListPriceExclVat: CellAt(0, i),
                DiscountPercent: null,
                UnitPriceExclVat: poZlave,
                UnitPriceInclVat: sDph,
                LineTotalExclVat: total,
                VatRate: vatRate,
                IsReverseCharge: vatRate == 0m,
                IsService: descr.Contains("Prenájom", StringComparison.OrdinalIgnoreCase)
                           || descr.Contains("Zľava", StringComparison.OrdinalIgnoreCase),
                Confidence: 0.4f));
        }
        return lines;
    }

    /// <summary>
    /// Route to a supplier-specific line parser, or null to fall back to the
    /// general parser. Matches by IČO when it could be extracted, but also by
    /// supplier name — some layouts (e.g. HEKTRANS / Doklado.sk) split the
    /// "IČO:" label from its value, so the IČO is unreliable while the company
    /// name is clear.
    /// </summary>
    private IReadOnlyList<ParsedDeliveryList>? TryParseBySupplier(
        ParsedInvoiceHeader header, string text, IReadOnlyList<DocumentAiEntity> entities)
    {
        var ico  = new string((header.SupplierIco ?? "").Where(char.IsDigit).ToArray());
        var name = (header.SupplierName ?? "").ToLowerInvariant();

        if (ico == IcoBauArticel || (name.Contains("bau") && name.Contains("articel")))
            return ParseSunSoftDeliveryLists(text, entities, header);
        if (ico == IcoHektrans || name.Contains("hektrans"))
            return ParseHektransDeliveryLists(text, entities, header);
        if (ico == IcoHilti || name.Contains("hilti"))
            return ParseHiltiDeliveryLists(text, entities, header);
        return null;
    }

    // ─── Hilti Slovakia layout ─────────────────────────────────────
    // Per-item summary label: the item's post-discount net. Document AI's
    // line_item amount is the PRE-discount gross (qty × list price), so the
    // labelled value is the one that reconciles.
    private static readonly Regex HiltiItemNetRx = new(
        @"Netto\s+hodnota\s+položky\s*:?\s*\n?\s*(-?\d{1,3}(?:[  ]\d{3})*,\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HiltiVatRateRx = new(
        @"Výstupná\s+DPH\s+(\d{1,2})\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HiltiMoneyLineRx = new(
        @"^\s*(-?\d{1,3}(?:[  ]\d{3})*,\d{2})\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Hilti Slovakia invoice (IČO 31344445). Single-table layout, no
    /// "za dodací list" grouping. Two quirks drive this parser:
    ///  1. Document AI's line_item amount is the PRE-discount gross (28,59 on
    ///     the reference 1841020753) — the post-discount net is printed under
    ///     the "Netto hodnota položky" label, one per item, in item order.
    ///  2. The summary block ("Manipulačný poplatok" / "Cena bez DPH" /
    ///     "Výstupná DPH x%") has NO EUR suffixes, and photo-scan OCR reflows
    ///     it as a run of label lines followed by a run of value lines — so
    ///     labels are paired with bare money lines in FIFO order. The
    ///     Manipulačný poplatok is a real charge and becomes its own line,
    ///     otherwise the invoice can never reconcile.
    /// Falls back to the general parser when nothing usable is found.
    /// </summary>
    private IReadOnlyList<ParsedDeliveryList> ParseHiltiDeliveryLists(
        string text, IReadOnlyList<DocumentAiEntity> entities, ParsedInvoiceHeader header)
    {
        var raw = entities
            .Where(e => string.Equals(e.Type, "line_item", StringComparison.OrdinalIgnoreCase) && !IsLikelyJunkLine(e))
            .Select(e => (
                Code: Prop(e, "product_code")?.Trim(),
                Desc: Prop(e, "description")?.Trim(),
                Qty: SlovakNumberHelper.TryParse(Prop(e, "quantity")),
                UnitPrice: SlovakNumberHelper.TryParse(Prop(e, "unit_price")),
                Unit: Prop(e, "unit"),
                Amount: SlovakNumberHelper.TryParse(Prop(e, "amount"))))
            .ToList();

        // Photo OCR sometimes splits one row into a code/description entity
        // and a separate quantity/price entity (the 2026-07-08 re-scan of
        // 1841020753 did exactly that). Merge such adjacent halves, then drop
        // whatever still has no quantity or no identity — those are phantoms.
        var merged = new List<(string? Code, string? Desc, decimal? Qty, decimal? UnitPrice, string? Unit, decimal? Amount)>();
        for (var i = 0; i < raw.Count; i++)
        {
            var cur = raw[i];
            var curHasIdentity = !string.IsNullOrEmpty(cur.Code) || !string.IsNullOrEmpty(cur.Desc);
            if (curHasIdentity && (cur.Qty ?? 0m) == 0m && i + 1 < raw.Count)
            {
                var nxt = raw[i + 1];
                if ((nxt.Qty ?? 0m) > 0m && string.IsNullOrEmpty(nxt.Code) && string.IsNullOrEmpty(nxt.Desc))
                {
                    merged.Add((cur.Code, cur.Desc, nxt.Qty, nxt.UnitPrice, nxt.Unit, nxt.Amount));
                    i++;
                    continue;
                }
            }
            merged.Add(cur);
        }
        var items = merged
            .Where(x => (x.Qty ?? 0m) > 0m && (!string.IsNullOrEmpty(x.Code) || !string.IsNullOrEmpty(x.Desc)))
            .ToList();

        // Post-discount nets, in item order.
        var nets = HiltiItemNetRx.Matches(text)
            .Select(m => SlovakNumberHelper.TryParse(m.Groups[1].Value))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        var vatRate = 23m;
        var vatRateM = HiltiVatRateRx.Match(text);
        if (vatRateM.Success) vatRate = decimal.Parse(vatRateM.Groups[1].Value, CultureInfo.InvariantCulture);

        // Summary block: pair label lines with bare money lines in FIFO order.
        // Inline "label value" forms are handled directly.
        decimal? fee = null, totalExcl = null, totalVat = null;
        var pending = new Queue<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            string? label = null;
            if (line.StartsWith("Manipulačný poplatok", StringComparison.OrdinalIgnoreCase)) label = "fee";
            else if (line.StartsWith("Cena bez DPH", StringComparison.OrdinalIgnoreCase)) label = "excl";
            else if (HiltiVatRateRx.IsMatch(line)) label = "vat";

            if (label != null)
            {
                // Inline value on the same line?
                var inline = Regex.Match(line, @"(-?\d{1,3}(?:[  ]\d{3})*,\d{2})\s*$");
                if (inline.Success) Assign(label, SlovakNumberHelper.TryParse(inline.Groups[1].Value));
                else pending.Enqueue(label);
                continue;
            }

            var money = HiltiMoneyLineRx.Match(line);
            if (money.Success && pending.Count > 0)
                Assign(pending.Dequeue(), SlovakNumberHelper.TryParse(money.Groups[1].Value));
        }
        void Assign(string which, decimal? v)
        {
            if (!v.HasValue) return;
            if (which == "fee") fee ??= v;
            else if (which == "excl") totalExcl ??= v;
            else totalVat ??= v;
        }

        if (items.Count == 0 && fee is null)
            return ParseDeliveryLists(text, entities);   // nothing usable → general parser

        var lines = new List<ParsedLine>(items.Count + 1);
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            // The labelled net beats Document AI's pre-discount amount, but
            // only when the counts line up (conservative pairing).
            var total = nets.Count == items.Count ? nets[i] : it.Amount;
            var qty = it.Qty!.Value;
            // Every Hilti item carries a customs-tariff footnote that OCR
            // appends to the description — cut it off.
            var descr = it.Desc ?? "";
            var cut = descr.IndexOf("Spoločný colný sadzobník", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0) descr = descr[..cut];
            descr = Regex.Replace(descr, @"\s+", " ").Trim();
            lines.Add(new ParsedLine(
                SupplierItemCode: it.Code.OrNull(),
                Description: string.IsNullOrWhiteSpace(descr) ? "(bez popisu)" : descr,
                Quantity: qty,
                Unit: string.IsNullOrWhiteSpace(it.Unit) ? "ks" : it.Unit!.Trim(),
                ListPriceExclVat: it.UnitPrice,
                DiscountPercent: null,
                UnitPriceExclVat: total.HasValue && qty != 0m
                    ? Math.Round(total.Value / qty, 4, MidpointRounding.AwayFromZero)
                    : it.UnitPrice,
                UnitPriceInclVat: null,
                LineTotalExclVat: total,
                VatRate: vatRate,
                IsReverseCharge: false,
                IsService: false,
                Confidence: 0.5f));
        }
        if (fee is { } f && f != 0m)
        {
            lines.Add(new ParsedLine(
                SupplierItemCode: null,
                Description: "Manipulačný poplatok",
                Quantity: 1m,
                Unit: "ks",
                ListPriceExclVat: f,
                DiscountPercent: null,
                UnitPriceExclVat: f,
                UnitPriceInclVat: null,
                LineTotalExclVat: f,
                VatRate: vatRate,
                IsReverseCharge: false,
                IsService: true,   // surcharge, not stock material
                Confidence: 0.5f));
        }

        return new List<ParsedDeliveryList>
        {
            new ParsedDeliveryList(
                DeliveryNoteRef: null,
                AkciaName: null,
                PickedUpBy: null,
                Note: null,
                DeliveryDate: header.DeliveryDate,
                SubtotalExclVat: totalExcl ?? lines.Sum(l => l.LineTotalExclVat ?? 0m),
                SubtotalVat: totalVat,
                Lines: lines)
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
    /// Receipt quantity repair: Document AI regularly maps the register row
    /// "1.0000 x 38.77 C 38.77" as quantity=38.77 / unit="C" (MPL 260210971
    /// photo scan) — the PRICE lands in the quantity column and the tax
    /// class becomes the unit. The printed rows carry the truth: when their
    /// count matches the carrying lines and a line's "quantity" equals the
    /// printed unit PRICE, take the row's quantity instead and re-derive the
    /// unit price from the (already net) line total. Receipt-gated; no-op
    /// for invoices.
    /// </summary>
    private static IReadOnlyList<ParsedDeliveryList> FixReceiptLineQuantities(IReadOnlyList<ParsedDeliveryList> dls, string text)
    {
        if (dls.Count != 1) return dls;
        var dl = dls[0];
        if (dl.DeliveryNoteRef != null || dl.Lines.Count == 0) return dls;
        if (!ReceiptKpMarkerRx.IsMatch(text) && !ReceiptInclRx.IsMatch(text)) return dls;

        // Printed register rows, in reading order. Matched over the WHOLE
        // text, not per line: photo reflow pushes the row total onto the
        // next line ("1.0000 X 38.77 C\n38.77" — MPL live photo) and the
        // regex's [\s.]* crosses that break.
        var rows = new List<(decimal? Qty, decimal Unit, decimal Total)>();
        foreach (Match m in ReceiptItemRowRx.Matches(text))
        {
            var qty = SlovakNumberHelper.TryParse(m.Groups["qty"].Value);
            var unit = SlovakNumberHelper.TryParse(m.Groups["unit"].Value);
            var total = SlovakNumberHelper.TryParse(m.Groups["total"].Value);
            if (unit is not { } u || total is not { } t) continue;
            if (qty is not { } q || Math.Abs(q * u - t) > 0.02m)
            {
                qty = null;
                if (u > 0m)
                {
                    var derived = t / u;
                    var rounded = Math.Round(derived, 0, MidpointRounding.AwayFromZero);
                    if (rounded >= 1m && Math.Abs(derived - rounded) <= 0.02m) qty = rounded;
                }
            }
            rows.Add((qty, u, t));
        }

        var carryingIdx = new List<int>();
        for (var i = 0; i < dl.Lines.Count; i++)
            if ((dl.Lines[i].LineTotalExclVat ?? 0m) > 0m) carryingIdx.Add(i);
        if (rows.Count == 0 || rows.Count != carryingIdx.Count) return dls;

        var newLines = dl.Lines.ToList();
        var changed = false;
        for (var k = 0; k < carryingIdx.Count; k++)
        {
            if (rows[k].Qty is not { } q) continue;
            var idx = carryingIdx[k];
            var line = newLines[idx];
            var lineQty = line.Quantity ?? 0m;
            // Intervene only when the parsed quantity is clearly the shuffle:
            // missing, or equal to the printed unit PRICE while differing
            // from the printed quantity.
            var suspicious = lineQty <= 0m
                             || (Math.Abs(lineQty - rows[k].Unit) <= 0.01m && Math.Abs(q - lineQty) > 0.01m);
            if (!suspicious) continue;

            var net = line.LineTotalExclVat ?? 0m;
            newLines[idx] = line with
            {
                Quantity = q,
                Unit = string.IsNullOrWhiteSpace(line.Unit) || line.Unit!.Trim().Length <= 1 ? "ks" : line.Unit,
                UnitPriceExclVat = q != 0m
                    ? Math.Round(net / q, 4, MidpointRounding.AwayFromZero)
                    : line.UnitPriceExclVat
            };
            changed = true;
        }
        return changed ? new List<ParsedDeliveryList> { dl with { Lines = newLines } } : dls;
    }

    /// <summary>
    /// Cash-receipt line normalization: receipts (HORNBACH / MPL / PRESPOR)
    /// print each line GROSS while the recap carries the net base — detected
    /// when the lines sum to base+VAT (±0,06 cash rounding). Each line is
    /// divided by the receipt's own VAT factor and any rounding residual
    /// lands on the largest line, so the lines reproduce the printed
    /// per-item "Základ" values. No-op for everything else.
    /// </summary>
    private static IReadOnlyList<ParsedDeliveryList> FixReceiptGrossLines(IReadOnlyList<ParsedDeliveryList> dls)
    {
        if (dls.Count != 1) return dls;
        var dl = dls[0];
        if (dl.DeliveryNoteRef != null) return dls;
        if (dl.SubtotalExclVat is not { } baseTotal || baseTotal <= 0m) return dls;
        if (dl.SubtotalVat is not { } vatTotal || vatTotal <= 0m) return dls;
        if (dl.Lines.Any(l => (l.LineTotalExclVat ?? 0m) < 0m)) return dls;   // refunds — stay out

        // Zero-total fragment rows (OCR junk that survived the filters) are
        // ignored by the arithmetic and left untouched — only the carrying
        // lines get converted (PRESPOR 145's phone scan carried two real
        // gross lines next to name fragments and bailed here before).
        var carrying = dl.Lines.Where(l => (l.LineTotalExclVat ?? 0m) > 0m).ToList();
        if (carrying.Count == 0) return dls;

        var sum = carrying.Sum(l => l.LineTotalExclVat!.Value);
        if (sum <= baseTotal || Math.Abs(sum - (baseTotal + vatTotal)) > 0.06m) return dls;

        var factor = 1m + vatTotal / baseTotal;
        var newLines = dl.Lines
            .Select(l =>
            {
                if ((l.LineTotalExclVat ?? 0m) <= 0m) return l;   // fragments stay as-is
                var net = Math.Round(l.LineTotalExclVat!.Value / factor, 2, MidpointRounding.AwayFromZero);
                var qty = l.Quantity ?? 0m;
                return l with
                {
                    LineTotalExclVat = net,
                    UnitPriceExclVat = qty != 0m
                        ? Math.Round(net / qty, 4, MidpointRounding.AwayFromZero)
                        : l.UnitPriceExclVat
                };
            })
            .ToList();
        var residual = Math.Round(baseTotal - newLines.Sum(l => l.LineTotalExclVat ?? 0m), 2, MidpointRounding.AwayFromZero);
        if (residual != 0m)
        {
            var idx = 0;
            for (var i = 1; i < newLines.Count; i++)
                if ((newLines[i].LineTotalExclVat ?? 0m) > (newLines[idx].LineTotalExclVat ?? 0m)) idx = i;
            newLines[idx] = newLines[idx] with { LineTotalExclVat = (newLines[idx].LineTotalExclVat ?? 0m) + residual };
        }
        return new List<ParsedDeliveryList> { dl with { Lines = newLines } };
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
            if (!dl.SubtotalExclVat.HasValue || dl.Lines.Count < 1)
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
                // No duplicate pair. Second signal: a single carrying line —
                // exactly one nonzero total (any others are phantom 0,00 rows)
                // or a lone zero line. Then that line must equal the printed
                // subtotal (KOVOUNI-BA: Document AI multiplied qty × the
                // wrong column and returned 2 377,18 for a 43,61 invoice).
                var nonZero = new List<int>();
                for (int k = 0; k < totals.Length; k++)
                    if (totals[k] != 0m) nonZero.Add(k);
                if (nonZero.Count == 1)
                {
                    // Only when every zero line is a quantity-less phantom —
                    // a real row whose amount alone was zeroed keeps the loud
                    // mismatch instead of silently absorbing its value here.
                    var othersArePhantoms = true;
                    for (int k = 0; k < dl.Lines.Count && othersArePhantoms; k++)
                        if (k != nonZero[0] && (dl.Lines[k].Quantity ?? 0m) != 0m)
                            othersArePhantoms = false;
                    if (othersArePhantoms) wrongIdx = nonZero[0];
                }
                else if (nonZero.Count == 0)
                {
                    // No line carries any amount (Document AI mapped no usable
                    // amount column — KOVOUNI-BA photo scan returned only a
                    // mismapped unit_price). If exactly one line is real
                    // (has a quantity), it owns the printed subtotal; the
                    // rest are quantity-less phantoms.
                    var realIdx = -1;
                    var realCount = 0;
                    for (int k = 0; k < dl.Lines.Count; k++)
                        if ((dl.Lines[k].Quantity ?? 0m) != 0m) { realIdx = k; realCount++; }
                    if (realCount == 1) wrongIdx = realIdx;
                    else if (totals.Length == 1) wrongIdx = 0;
                }
            }
            if (wrongIdx < 0)
            {
                // No safe signal for which line is wrong.
                corrected.Add(dl);
                continue;
            }

            var otherSum = 0m;
            for (int k = 0; k < totals.Length; k++)
                if (k != wrongIdx) otherSum += totals[k];
            var newTotal = Math.Round(dl.SubtotalExclVat.Value - otherSum, 2, MidpointRounding.AwayFromZero);

            var newLines = dl.Lines.ToList();
            var wrong = newLines[wrongIdx];
            // ParsedLine is a record — use 'with' to rebuild with the corrected
            // total. The unit price follows from it (the stored one may be the
            // mismapped column that caused the wrong total in the first place).
            var wrongQty = wrong.Quantity ?? 0m;
            newLines[wrongIdx] = wrong with
            {
                LineTotalExclVat = newTotal,
                UnitPriceExclVat = wrongQty != 0m
                    ? Math.Round(newTotal / wrongQty, 4, MidpointRounding.AwayFromZero)
                    : wrong.UnitPriceExclVat
            };
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
        // ^ matching after every newline. Photo OCR mutates "s.r.o." into
        // things like "s.г.0." (Cyrillic г, digit zero) — the suffix class
        // tolerates those and the result is normalised back to "s.r.o.".
        foreach (Match m in Regex.Matches(block,
            @"^([^\r\n]*?(?:s\.\s*[rг]\.\s*[oо0]\.?|a\.\s*s\.|k\.\s*s\.|spol\.\s*s\s*r\.\s*o\.)[^\r\n]*)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var line = NormalizeCompanySuffix(m.Groups[1].Value.Trim());
            if (line.Length >= 8
                && !line.Contains("ďakujem", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("dodávateľ", StringComparison.OrdinalIgnoreCase)
                && !OwnCompanyNameRx.IsMatch(line))
                return line;
        }
        return null;
    }

    /// <summary>OCR renders "s.r.o." with Cyrillic/digit lookalikes ("s.г.0.")
    /// — normalise the trailing company suffix back.</summary>
    private static string NormalizeCompanySuffix(string line)
        => Regex.Replace(line, @"s\.\s*[rг]\.\s*[oо0]\.?\s*$", "s.r.o.", RegexOptions.IgnoreCase);

    // ─── Header parser ─────────────────────────────────────────────

    private ParsedInvoiceHeader ParseHeader(string text, IReadOnlyList<DocumentAiEntity> entities)
    {
        // Prefer Document AI entities for fields it nails (invoice_id, supplier,
        // dates, totals) and use regex as fallback / for SK-specific fields.
        string? invoiceNumber = FindEntity(entities, "invoice_id")?.MentionText?.Trim()
                                ?? InvoiceNumberRx.Match(text).Groups[1].Value.OrNull()
                                ?? InvoiceNumberNearRx.Match(text).Groups[1].Value.OrNull()
                                // Cash receipts: č. bloku / číslo účtenky / č.d.
                                ?? ReceiptNumberRx.Match(text).Groups[1].Value.OrNull()
                                ?? ReceiptNumberBeforeDnaRx.Match(text).Groups[1].Value.OrNull();

        string? supplierName  = FindEntity(entities, "supplier_name")?.MentionText?.Trim();
        // Document AI's supplier-name heuristic occasionally picks the
        // closing line "ďakujeme za Váš nákup" or a single-word logo like
        // "DEK" instead of the actual supplier. Validate and fall back.
        var looksLikeNoise = supplierName != null && (
            supplierName.Contains("ďakujem", StringComparison.OrdinalIgnoreCase)
            || supplierName.Contains("faktúra", StringComparison.OrdinalIgnoreCase)      // document TITLE, not a name
            || supplierName.Contains("daňový doklad", StringComparison.OrdinalIgnoreCase) // (BAU-ARTICEL scan returned "Faktúra - daňový doklad")
            || OwnCompanyNameRx.IsMatch(supplierName)            // we can never be our own supplier
            || supplierName.Length < 8                          // single short word, e.g. "DEK"
            || !supplierName.Any(char.IsLetter)
            || !supplierName.Contains(' '));                     // no space → not a real company name
        if (string.IsNullOrWhiteSpace(supplierName) || looksLikeNoise)
        {
            // A noise value must never survive to the document — if every
            // fallback below fails, "(neznámy dodávateľ)" beats showing the
            // customer's own company as the supplier.
            supplierName = null;

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
                    else
                    {
                        // Cash receipts have no "dodávateľ" label at all and
                        // Document AI's supplier_name grabs whatever sits on
                        // top of the scan (the handwritten site note on
                        // PRESPOR 145, the logo on MPL, or the CUSTOMER's own
                        // block). The first company-suffix line in the
                        // document head that isn't us is the actual supplier
                        // ("PRESPOR spol. s r.o.", "KOVOUNI-BA s.r.o.").
                        var headText = text[..Math.Min(text.Length, 1200)];
                        foreach (Match m in Regex.Matches(headText,
                            @"^([^\r\n]*?(?:s\.\s*[rг]\.\s*[oо0]\.?|a\.\s*s\.|k\.\s*s\.|spol\.\s*s\s*r\.\s*o\.)[^\r\n]*)",
                            RegexOptions.Multiline | RegexOptions.IgnoreCase))
                        {
                            var line = Regex.Replace(NormalizeCompanySuffix(m.Groups[1].Value.Trim()),
                                @"^dodávateľ\s*:?\s*", "", RegexOptions.IgnoreCase);
                            if (line.Length >= 8
                                && !line.Contains("ďakujem", StringComparison.OrdinalIgnoreCase)
                                && !line.Contains("odberateľ", StringComparison.OrdinalIgnoreCase)
                                && !OwnCompanyNameRx.IsMatch(line))
                            {
                                supplierName = line;
                                break;
                            }
                        }
                    }
                }
            }
        }

        // IČO / IČ DPH / IBAN — Document AI does NOT extract these on SK invoices
        // by default. Regex-only.
        var icoMatches    = IcoRx.Matches(text);
        var icDphMatches  = IcDphRx.Matches(text);
        // First IČO on the page is usually the supplier (top-left block) —
        // but photo reflow can put the receiver's block first, so the own
        // company's identifiers are skipped outright. Not bulletproof —
        // manager edits on review.
        string? supplierIco = icoMatches
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(v => v != OwnCompanyIco);
        string? supplierIcDph = icDphMatches
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(v => !string.Equals(v, OwnCompanyIcDph, StringComparison.OrdinalIgnoreCase));
        string? supplierIban  = IbanRx.Match(text).Groups[1].Value.OrNull()?.Replace(" ", "");

        // Dates: prefer Document AI's normalized ISO when present.
        DateTime? issueDate    = ParseEntityDate(entities, "invoice_date")
                                 ?? ParseSkDate(IssueDateRx.Match(text).Groups[1].Value);
        DateTime? dueDate      = ParseEntityDate(entities, "due_date")
                                 ?? ParseSkDate(DueDateRx.Match(text).Groups[1].Value);
        DateTime? deliveryDate = ParseEntityDate(entities, "delivery_date")
                                 ?? ParseSkDate(HeaderDelDateRx.Match(text).Groups[1].Value);

        // Scrambled header dates (A-Z STAV photo layout): the labels OCR as
        // one run and the values as another, so the issue date's own regex
        // can't fire — and worse, HeaderDelDateRx then grabs the vystavenia
        // VALUE for the delivery date ("Dátum vystavenia:\nDátum dodania:\n
        // 12.5.2026\n9.5.2026"). A missing issue date is the telltale: pair
        // label occurrences with date tokens by order inside a bounded region
        // and let the pairs OVERRIDE the mispaired direct matches.
        if (issueDate == null)
        {
            var dateLabels = DateLabelRx.Matches(text).ToList();
            if (dateLabels.Count >= 2)
            {
                var regionStart = dateLabels[0].Index;
                var regionEnd = Math.Min(text.Length, regionStart + 500);
                var dateValues = DateTokenRx.Matches(text)
                    .Where(m => m.Index >= regionStart && m.Index < regionEnd)
                    .Select(m => ParseSkDate(m.Value))
                    .Where(d => d.HasValue)
                    .ToList();
                var n = Math.Min(dateLabels.Count, dateValues.Count);
                DateTime? pIssue = null, pDelivery = null, pDue = null;
                for (var i = 0; i < n; i++)
                {
                    var kind = dateLabels[i].Groups["kind"].Value.ToLowerInvariant();
                    if (kind is "vyhotovenia" or "vydania" or "vystavenia" or "vyhotovenie") pIssue ??= dateValues[i];
                    else if (kind is "dodania" or "dodanie") pDelivery ??= dateValues[i];
                    else pDue ??= dateValues[i];
                }
                if (pIssue != null) issueDate = pIssue;
                if (pDelivery != null) deliveryDate = pDelivery;
                if (pDue != null) dueDate = pDue;
            }
        }

        // Cash-receipt date forms: "dňa: 25.05.2026" or dashed "03-06-2026".
        if (issueDate == null)
        {
            issueDate = ParseSkDate(ReceiptDateRx.Match(text).Groups[1].Value);
            if (issueDate == null)
            {
                var dashM = ReceiptDashDateRx.Match(text);
                if (dashM.Success)
                    issueDate = ParseSkDate($"{dashM.Groups[1].Value}.{dashM.Groups[2].Value}.{dashM.Groups[3].Value}");
            }
        }

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

        // Per-rate summary fallback (A-Z STAV layout): base + VAT + incl,
        // filling whichever total is still missing.
        var vatRow = ExtractVatSummaryTotals(text);
        totalExclVat ??= vatRow.Base;
        totalVat     ??= vatRow.Vat;
        var vatRowIncl = vatRow.Incl;

        // Grand total (incl. VAT). Two candidates: the labelled text total
        // ("celkom k úhrade …", "suma na úhradu …", DEK's zaokrúhlenie block)
        // and Document AI's total_amount entity. Each fails differently — the
        // entity can grab a stray small figure like a weighbridge "TOTAL 1.65 t"
        // on an extra page, while the text regex can misfire on an unusual
        // layout. The grand incl-VAT total is the LARGEST monetary figure on an
        // invoice, so when both parse we take the larger: that rejects the tiny
        // weighbridge value (BAU) without ever dropping below the reliable
        // entity total on suppliers like DEK.
        var textTotal   = SlovakNumberHelper.TryParse(TotalInclVatRx.Match(text).Groups[1].Value)
                          ?? vatRowIncl
                          // Cash receipts: "NA ÚHRADU EUR 15,65" / "Spolu v EUR 38.75"
                          ?? SlovakNumberHelper.TryParse(ReceiptInclRx.Match(text).Groups[1].Value);
        var entityTotal = SlovakNumberHelper.TryParse(FindEntity(entities, "total_amount")?.MentionText);
        decimal? totalInclVat = (textTotal, entityTotal) switch
        {
            (decimal a, decimal b) => Math.Max(a, b),
            (decimal a, null)      => a,
            (null, decimal b)      => b,
            _                      => null
        };

        // Cash-receipt VAT recap fallback ("C 23% 31.52 7.25 38.77"): fills
        // base/VAT when nothing above found them — and OVERRIDES a degenerate
        // header where excl == incl with zero/absent VAT: Document AI returns
        // the PAID total as net_amount on receipts (MPL 260210971), which
        // would store the gross as the material cost. Reverse-charge invoices
        // (KOVOUNI) have no such recap row, so they keep their genuine
        // excl == incl.
        if (totalExclVat == null || totalVat == null
            || (totalVat == 0m && totalExclVat == totalInclVat))
        {
            var recap = ExtractReceiptRecapTotals(text, totalInclVat);
            if (recap.Base.HasValue && recap.Vat is > 0m
                && (totalInclVat is not { } incl
                    || Math.Abs(recap.Base.Value + recap.Vat.Value - incl) <= 0.06m))
            {
                totalExclVat = recap.Base;
                totalVat     = recap.Vat;
            }
            else
            {
                totalExclVat ??= recap.Base;
                totalVat     ??= recap.Vat;
            }
        }

        // If VAT total is missing but excl + incl are present, derive it.
        if (totalVat == null && totalExclVat.HasValue && totalInclVat.HasValue)
            totalVat = totalInclVat.Value - totalExclVat.Value;

        // Consistency guard: when all three parsed but don't add up, the VAT
        // is the least reliably anchored of the three (photo-scan OCR loves
        // to duplicate the base into it) — recompute it from excl and incl.
        if (totalExclVat.HasValue && totalVat.HasValue && totalInclVat.HasValue
            && Math.Abs(totalExclVat.Value + totalVat.Value - totalInclVat.Value) > 0.02m
            && totalInclVat.Value >= totalExclVat.Value)
            totalVat = totalInclVat.Value - totalExclVat.Value;

        var currency = FindEntity(entities, "currency")?.MentionText?.Trim().ToUpperInvariant() ?? "EUR";

        // Cash receipt vs invoice: eKasa code or receipt-style total label.
        var isReceipt = ReceiptKpMarkerRx.IsMatch(text) || ReceiptInclRx.IsMatch(text);

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
            Currency: currency,
            IsReceipt: isReceipt);
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
            var lineOffsets = new List<int>();
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
                lineOffsets.Add(off);
            }

            // Fragmented rows: OCR sometimes splits one row into a
            // description-only entity and a quantity entity whose
            // "description" is just the quantity text ("27,680 tona" on
            // A-Z STAV 26200942). With exactly one of each in the delivery
            // list, merge them into one properly named line.
            if (linesForThisGroup.Count >= 2)
            {
                int descFragIdx = -1, needsNameIdx = -1, descFrags = 0, needsName = 0;
                for (var i = 0; i < linesForThisGroup.Count; i++)
                {
                    var l = linesForThisGroup[i];
                    var hasQty = (l.Quantity ?? 0m) != 0m;
                    var hasAmt = (l.LineTotalExclVat ?? 0m) != 0m;
                    if (!hasQty && !hasAmt && l.Description.Count(char.IsLetter) >= 3)
                    {
                        descFrags++;
                        descFragIdx = i;
                    }
                    else if (hasQty && Regex.IsMatch(l.Description, @"^[\d\s.,]*\p{L}{0,6}$"))
                    {
                        needsName++;
                        needsNameIdx = i;
                    }
                }
                if (descFrags == 1 && needsName == 1)
                {
                    var frag = linesForThisGroup[descFragIdx];
                    var target = linesForThisGroup[needsNameIdx];
                    linesForThisGroup[needsNameIdx] = target with
                    {
                        Description = Regex.Replace(frag.Description, @"\s+", " ").Trim(),
                        SupplierItemCode = target.SupplierItemCode ?? frag.SupplierItemCode
                    };
                    linesForThisGroup.RemoveAt(descFragIdx);
                    lineOffsets.RemoveAt(descFragIdx);
                }
            }

            // No name fragment anywhere — some scans return ONLY numeric
            // entities (A-Z STAV 26200942's third scan gave qty / unit_price /
            // amount and nothing else). For a lone line the name is the text
            // block directly above its anchor, between the table header words.
            if (linesForThisGroup.Count == 1
                && Regex.IsMatch(linesForThisGroup[0].Description, @"^[\d\s.,]*\p{L}{0,6}$")
                && (linesForThisGroup[0].Quantity ?? 0m) != 0m)
            {
                var nameFromText = ExtractNameAboveAnchor(text, lineOffsets[0]);
                if (nameFromText != null)
                    linesForThisGroup[0] = linesForThisGroup[0] with { Description = nameFromText };
            }

            // A delivery list whose printed subtotal carries no VAT at all
            // (only "základ DPH 0 %" — full reverse charge) cannot contain a
            // VAT-bearing line. Force 0 % on every line: the per-row uplift
            // check can't always fire because Document AI sometimes reflows
            // the price cells past the printed subtotal, i.e. beyond rowEnd
            // (FA 2600141367 / DL-100-26-015918).
            if (meta.SubVat == 0m && (meta.SubExclVat ?? 0m) > 0m)
                for (var i = 0; i < linesForThisGroup.Count; i++)
                    linesForThisGroup[i] = linesForThisGroup[i] with { VatRate = 0m, IsReverseCharge = true };

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

        // Detect reverse-charge (prenesenie daňovej povinnosti → 0% VAT). The
        // "**" marker is authoritative when Document AI keeps it, but it usually
        // strips the prefix — which is why relying on it alone (or on a hardcoded
        // product name) misclassified other reverse-charge items and broke the
        // cent-level reconciliation. The format-independent signal is the price
        // row itself: a reverse-charge line has no VAT uplift, so "cena po zľave
        // s DPH" equals "cena po zľave bez DPH". Only trust that inside a segment
        // whose printed subtotal actually carries a 0% rate, so a 1-cent rounding
        // coincidence on a normal line can't misfire.
        bool noVatUplift = rowPrices.HasValue
                           && Math.Abs(rowPrices.Value.postIncl - rowPrices.Value.postExcl) <= 0.01m;
        bool isReverse = (descr?.Contains("**") ?? false)
                         || (supplierCd?.Contains("**") ?? false)
                         || (segmentHasReverseCharge && noVatUplift);

        var vatRate = isReverse ? 0m : defaultVat;

        var codeClean = supplierCd?.Trim().TrimStart('*').Trim();
        var descClean = DedupRepeatedDescription((descr ?? "").Trim().TrimStart('*').Trim());
        // Receipts: the PLU alone often arrives as the whole "description"
        // (PRESPOR "7760332") — that's a code, not a name. Keep it as the
        // item code; the name stays blank for the manager to fill in.
        if (descClean.Length > 0 && descClean.Any(char.IsDigit) && !descClean.Any(char.IsLetter))
        {
            codeClean ??= descClean;
            descClean = "";
        }

        return new ParsedLine(
            SupplierItemCode: codeClean,
            Description: descClean,
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
        // Anchor marker: the description property, or — when the OCR
        // fragmented the row and this piece carries none (e.g. a bare
        // "27,680 tona" quantity fragment on the A-Z STAV photo scan) — the
        // entity's own mention text. Without a marker the line would be
        // silently dropped.
        var descrRaw = e.Properties.FirstOrDefault(p => p.Type.EndsWith("/description", StringComparison.Ordinal))?.MentionText
                       ?? e.MentionText;
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

        // Nothing at or after the cursor. Document AI's entity order doesn't
        // always follow reading order — a fragment whose text sits BEFORE an
        // already-anchored entity would be dropped entirely (A-Z STAV
        // 26200942: the name fragment precedes the quantity fragment). Retry
        // once from the top of the document.
        if (fromIndex > 0) return FindNextOffset(e, text, 0);

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
