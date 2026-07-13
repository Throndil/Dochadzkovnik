using System.Globalization;
using API.Services;
using Xunit;

namespace API.Tests;

/// <summary>
/// Text-layer regression tests for <see cref="InvoiceParser"/>.
///
/// SCOPE AND CAVEAT. The production parser consumes Google Document AI's
/// reflowed FullText PLUS its structured line_item entities. These tests feed
/// the parser a <see cref="DocumentAiResult"/> built from each invoice's real
/// text layer (pdftotext output, the closest offline proxy for DocAI FullText)
/// with an EMPTY entity list. That deliberately exercises only the
/// text-pattern half of the parser: header fields, IČO/IČ DPH/IBAN, dates,
/// totals, "za dodací list" segmentation and "akcia:" worksite extraction.
/// Line-item extraction (quantities, the 5-column price block, per-line
/// reconciliation) needs the DocAI entities and is NOT covered here — capture
/// the live processor's RawJson into fixtures/ if you want true end-to-end
/// tests.
///
/// What these tests lock down:
///   - The two DEK "Súhrnná faktúra" clones (FA_2600150614, FA_2600132372)
///     parse like the binding master: header + segmentation + grand total.
///   - The HEKTRANS single-table invoice parses header + totals via the V2
///     label alternations (Dátum vydania, Základ DPH / Suma na úhradu, €).
///     The BAU-ARTICEL image-only scan stays OCR-dependent (no text layer).
///
/// Run:  dotnet test API.Tests/API.Tests.csproj
/// </summary>
public class InvoiceParserTextLayerTests
{
    private static readonly InvoiceParser Parser = new();

    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Assert.True(File.Exists(path), $"Missing fixture: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Build an OCR result with real text and no entities (text-layer-only test seam).</summary>
    private static ParsedInvoice ParseTextOnly(string fixtureName)
        => Parser.Parse(new DocumentAiResult(
            RawJson: "{}",
            Entities: Array.Empty<DocumentAiEntity>(),
            FullText: Fixture(fixtureName)));

    // ───────────────────────── DEK clones: must parse like the master ─────────

    // Per-fixture expectations verified against the fixtures with
    // API.Tests/tools/replay_regexes.py (DL/akcia counts, dates, totals).
    [Theory]
    [InlineData("FA_2600141367.txt", "2600141367", 1507.63, 1788.43, 13, 12, "23.5.2026", "21.6.2026")] // binding master
    [InlineData("FA_2600150614.txt", "2600150614", 559.50, 657.16, 9, 9, "30.5.2026", "28.6.2026")]
    [InlineData("FA_2600132372.txt", "2600132372", 329.33, 402.37, 8, 8, "16.5.2026", "13.6.2026")]
    public void DekInvoices_ParseHeader_AndSegmentDeliveryLists(
        string fixture, string expectedInvoiceNo, double expectedExclVat, double expectedInclVat,
        int expectedDlCount, int expectedAkciaCount, string expectedIssueDate, string expectedDueDate)
    {
        var inv = ParseTextOnly(fixture);

        Assert.Equal(expectedInvoiceNo, inv.Header.InvoiceNumber);
        Assert.Equal("43821103", inv.Header.SupplierIco);
        Assert.Equal("SK2022484849", inv.Header.SupplierIcDph);
        Assert.False(string.IsNullOrEmpty(inv.Header.SupplierIban));
        // NOTE: supplier *name* is intentionally not asserted here. On DEK's
        // layout the name sits above the "dodávateľ" label, so the text-only
        // block scan misses it; production fills SupplierName from Document
        // AI's supplier_name entity, which this text-only seam does not supply.

        // "dátum vyhotovenia" / "dátum splatnosti" (SK d.M.yyyy).
        Assert.Equal(DateTime.ParseExact(expectedIssueDate, "d.M.yyyy", CultureInfo.InvariantCulture),
            inv.Header.IssueDate);
        Assert.Equal(DateTime.ParseExact(expectedDueDate, "d.M.yyyy", CultureInfo.InvariantCulture),
            inv.Header.DueDate);

        // "za dodací list" segmentation: one parsed group per printed DL header.
        Assert.Equal(expectedDlCount, inv.DeliveryLists.Count);
        Assert.All(inv.DeliveryLists, dl => Assert.False(string.IsNullOrEmpty(dl.DeliveryNoteRef)));

        // Every DL carries its printed "základ DPH X% … EUR | DPH X% … EUR"
        // subtotal, and the per-DL subtotals reconcile to the grand totals
        // (base sums to the excl-VAT total, VAT sums to incl − excl).
        Assert.All(inv.DeliveryLists, dl => Assert.True(dl.SubtotalExclVat.HasValue));
        Assert.Equal((decimal)expectedExclVat, inv.DeliveryLists.Sum(dl => dl.SubtotalExclVat ?? 0m), 2);
        Assert.Equal((decimal)expectedInclVat - (decimal)expectedExclVat,
            inv.DeliveryLists.Sum(dl => dl.SubtotalVat ?? 0m), 2);

        // "akcia:" worksite extraction (the master has one DL with a "." sentinel → null).
        Assert.Equal(expectedAkciaCount,
            inv.DeliveryLists.Count(dl => !string.IsNullOrWhiteSpace(dl.AkciaName)));

        // Grand total excl VAT comes from the "cena bez DPH … EUR" footer.
        Assert.NotNull(inv.Header.TotalExclVat);
        Assert.Equal((decimal)expectedExclVat, inv.Header.TotalExclVat!.Value, 2);

        // Grand total incl VAT: the "spolu" amount right after the
        // "zaokrúhlenie" rounding line (V2 fixed the latent TotalInclVatRx
        // bug — it used to match nothing here, or the 0,00 rounding line).
        Assert.NotNull(inv.Header.TotalInclVat);
        Assert.Equal((decimal)expectedInclVat, inv.Header.TotalInclVat!.Value, 2);
    }

    [Fact]
    public void DekInvoice_ExtractsAkciaWorksite_OnAtLeastOneDeliveryList()
    {
        var inv = ParseTextOnly("FA_2600150614.txt");
        Assert.Contains(inv.DeliveryLists, dl => !string.IsNullOrWhiteSpace(dl.AkciaName));
    }

    [Fact]
    public void DekInvoice_PerDeliveryListSubtotals_AreExtracted()
    {
        var inv = ParseTextOnly("FA_2600132372.txt");
        // At least most lists carry a "základ DPH X% … EUR | DPH X% … EUR" subtotal.
        Assert.Contains(inv.DeliveryLists, dl => dl.SubtotalExclVat.HasValue);
    }

    // ──────────────────── Cash receipts (pokladničné bloky) ───────────────────
    // Real text layers captured from rejected photo uploads (2026-07-13).
    // Receipts differ from invoices: paid total labelled with EUR BEFORE the
    // value, dot decimals, per-class VAT recap, č.bloku/č.d. document
    // numbers, "dňa:"/dashed dates, and GROSS line prices.

    [Fact]
    public void Receipt_Mpl_HeaderParses_AndGrossLineBecomesNet()
    {
        var text = Fixture("RECEIPT_MPL_260210971.txt");
        var entities = new[]
        {
            // Live: supplier_name came back as the OCR'd logo (no space →
            // noise) — the company-suffix head line must win.
            new DocumentAiEntity("supplier_name", "Ampl\nSTAVEBNINY", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            // Document AI returns the PAID total as net_amount on receipts —
            // without the recap override this would store 38,75 gross as the
            // material cost (live upload id 55, 2026-07-13).
            new DocumentAiEntity("net_amount", "38.75", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("total_amount", "38.75", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "MAPEI Eco Prim Grip Plus - 5kg 1.0000 X 38.77 C 38.77", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "MAPEI Eco Prim Grip Plus - 5kg", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "1", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "38.77", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        Assert.True(inv.Header.IsReceipt);                     // KPEKK marker
        Assert.Equal("MPL TRADING spol. s r.o.", inv.Header.SupplierName);
        Assert.Equal("260210971", inv.Header.InvoiceNumber);   // "Faktúra: OFGA.26 260210971"
        Assert.Equal(new DateTime(2026, 5, 25), inv.Header.IssueDate);   // "dňa: 25.05.2026"
        Assert.Equal(31.52m, inv.Header.TotalExclVat);         // recap "C 23% 31.52 7.25 38.77"
        Assert.Equal(7.25m, inv.Header.TotalVat);
        Assert.Equal(38.75m, inv.Header.TotalInclVat);         // "Spolu v EUR 38.75" (po zaokrúhlení)

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(31.52m, dl.SubtotalExclVat);
        Assert.Equal(7.25m, dl.SubtotalVat);
        var line = Assert.Single(dl.Lines);
        Assert.Equal(31.52m, line.LineTotalExclVat);           // gross 38.77 → net
        Assert.Equal(23m, line.VatRate);
    }

    [Fact]
    public void Receipt_Mpl_ShuffledQuantityColumns_AreRepairedFromThePrintedRow()
    {
        // Live photo upload (2026-07-13, invoice 97): Document AI mapped the
        // register row as quantity=38.77 / unit="C" — the PRICE in the
        // quantity column. The photo text also reflows the row total onto
        // the NEXT line ("1.0000 X 38.77 C\n38.77"), so the printed-row scan
        // must work across the break. The printed row wins: quantity 1,
        // unit ks, unit price re-derived from the net total.
        var text = Fixture("RECEIPT_MPL_260210971_PHOTO.txt");
        var entities = new[]
        {
            new DocumentAiEntity("net_amount", "38.75", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "MAPEI Eco Prim Grip Plus - 5kg 38.77 C 38.77", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "MAPEI Eco Prim Grip Plus - 5kg", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "38.77", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit", "C", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "38.77", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        var dl = Assert.Single(inv.DeliveryLists);
        var line = Assert.Single(dl.Lines);
        Assert.Equal(31.52m, line.LineTotalExclVat);   // gross 38.77 → net
        Assert.Equal(1m, line.Quantity);               // not 38.77
        Assert.Equal("ks", line.Unit);                 // not the tax class "C"
        Assert.Equal(31.52m, line.UnitPriceExclVat);   // net ÷ qty, not 0.81
        Assert.Equal(23m, line.VatRate);
    }

    [Fact]
    public void Receipt_Hornbach_HeaderParses_FromBlockNumberDashDateAndReflowedRecap()
    {
        var inv = ParseTextOnly("RECEIPT_HORNBACH_815.txt");

        Assert.True(inv.Header.IsReceipt);                     // KP marker + "NA ÚHRADU EUR"
        Assert.Equal("HORNBACH - Baumarkt SK spol. s r.o.", inv.Header.SupplierName);
        Assert.Equal("815", inv.Header.InvoiceNumber);         // "č.bloku: 815"
        Assert.Equal(new DateTime(2026, 6, 3), inv.Header.IssueDate);    // "03-06-2026"
        Assert.Equal(12.73m, inv.Header.TotalExclVat);         // recap "A 23% … 12,73 2,93"
        Assert.Equal(2.93m, inv.Header.TotalVat);
        Assert.Equal(15.65m, inv.Header.TotalInclVat);         // "NA ÚHRADU EUR 15,65"

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(12.73m, dl.SubtotalExclVat);
        Assert.Equal(2.93m, dl.SubtotalVat);

        // HORNBACH layout: no "×" register rows — items recovered from the
        // per-item "Základ" net lines + "… 1 Kus" name lines + EANs (live
        // photo upload mapped the GROSS as quantity and the recap echoes
        // MEDZISÚČET/NA ÚHRADU/HOTOVOSŤ as items).
        Assert.Equal(9, dl.Lines.Count);
        Assert.Equal("107H Sifón pračkový", dl.Lines[0].Description);
        Assert.Equal("8595580502973", dl.Lines[0].SupplierItemCode);
        Assert.Equal(1m, dl.Lines[0].Quantity);
        Assert.Equal("ks", dl.Lines[0].Unit);
        Assert.Equal(2.60m, dl.Lines[0].LineTotalExclVat);
        Assert.Equal(2.60m, dl.Lines[0].UnitPriceExclVat);
        Assert.Equal(23m, dl.Lines[0].VatRate);
        Assert.Equal("HMOŽDINKA GKS K, 5XG", dl.Lines[1].Description);
        Assert.Equal(3.33m, dl.Lines[1].LineTotalExclVat);
        Assert.All(dl.Lines.Skip(2).Take(6), l =>
        {
            Assert.Equal(0.33m, l.LineTotalExclVat);
            Assert.Equal(1m, l.Quantity);
        });
        Assert.Equal("DVIERKA VAŇOVÉ PVC D", dl.Lines[8].Description);
        // Per-item rounding residual (Základy 12,75 vs recap 12,73) lands on
        // the largest line → the list is cent-exact against the base.
        Assert.Equal(4.82m, dl.Lines[8].LineTotalExclVat);
        Assert.Equal(12.73m, dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m));
    }

    [Fact]
    public void Receipt_PresporNoisyPhone_Scan_TotalParsesDespiteOcrJunkAfterLabel()
    {
        // Phone-camera scan of the same receipt (2026-07-13, rejected live):
        // stamp bleed-through injected junk between the label and the value
        // ("Spolu v EUR dego / 59.10"), which the strict label-adjacent regex
        // missed — the upload was rejected for a missing total even though
        // the sum sat right there. A bounded non-digit gap now covers it.
        var inv = ParseTextOnly("RECEIPT_PRESPOR_145_NOISY.txt");

        Assert.Equal("145", inv.Header.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 6, 4), inv.Header.IssueDate);
        Assert.Equal("PRESPOR spol. s r.o.", inv.Header.SupplierName);
        Assert.Equal(48.06m, inv.Header.TotalExclVat);
        Assert.Equal(11.05m, inv.Header.TotalVat);
        Assert.Equal(59.10m, inv.Header.TotalInclVat);   // the upload gate passes now

        // Live upload (invoice 101): the second row's total is severed from
        // its head ("ZLAVA, 10% 1.00 × 28.55 с │ 7120914 │ x C │ 28.55") —
        // the orphan-head pairing must still rebuild BOTH items, discounts
        // included, and the gross rows must divide down to the printed base.
        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(2, dl.Lines.Count);
        Assert.Equal("PS.FSB6 MONDRILLO BLACK diamant,kor.za s ucha", dl.Lines[0].Description);
        Assert.Equal("7760332", dl.Lines[0].SupplierItemCode);
        Assert.Equal(1m, dl.Lines[0].Quantity);
        Assert.Equal(24.85m, dl.Lines[0].LineTotalExclVat);   // gross 30.56 → net
        Assert.Equal(10m, dl.Lines[0].DiscountPercent);
        Assert.Equal("CO.Dia vrtak 6,0 PROJAHN M14", dl.Lines[1].Description);
        Assert.Equal(1m, dl.Lines[1].Quantity);
        Assert.Equal(23.21m, dl.Lines[1].LineTotalExclVat);   // gross 28.55 → net
        Assert.Equal(10m, dl.Lines[1].DiscountPercent);       // "ZLAVA, 10%" (comma)
        Assert.Equal(48.06m, dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m));
    }

    [Fact]
    public void Receipt_Prespor_NumberBeforeDna_AndBothGrossLinesBecomeNet()
    {
        // The "č.d.:" label got reflowed away from its value — the number sits
        // directly before "dňa:". Both lines arrive GROSS (30.56 + 28.55 =
        // 59.11 = base + VAT) and must divide down to the printed base
        // (24.85 + 23.21 = 48.06).
        var text = Fixture("RECEIPT_PRESPOR_145.txt");
        var entities = new[]
        {
            new DocumentAiEntity("line_item", "PS.FSB6 MONDRILLO BLACK diamant,kor.za sucha", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "PS.FSB6 MONDRILLO BLACK diamant,kor.za sucha", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "1", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "30.56", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            new DocumentAiEntity("line_item", "CO.Dia vrtak 6,0 PROJAHN M14", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "CO.Dia vrtak 6,0 PROJAHN M14", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "1", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "28.55", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        Assert.Equal("145", inv.Header.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 6, 4), inv.Header.IssueDate);    // "dňa: 04.06.2026"
        Assert.Equal(48.06m, inv.Header.TotalExclVat);
        Assert.Equal(11.05m, inv.Header.TotalVat);
        Assert.Equal(59.10m, inv.Header.TotalInclVat);         // "Spolu v EUR 59.10"

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(2, dl.Lines.Count);
        Assert.Equal(24.85m, dl.Lines[0].LineTotalExclVat);
        Assert.Equal(23.21m, dl.Lines[1].LineTotalExclVat);
        Assert.Equal(48.06m, dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m));
        // Reconciliation as the review computes it: 48.06 + (5.72 + 5.34) VAT
        // = 59.12 vs printed 59.10 — inside the 5-cent cash-rounding
        // tolerance used by the server.
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.True(Math.Abs(48.06m + vat - 59.10m) <= 0.05m);
    }

    [Fact]
    public void Receipt_Prespor_BrokenEntityLines_AreRebuiltFromText_BothItems()
    {
        // Live upload id 57 (2026-07-13): Document AI returned only the FIRST
        // item (gross 30,56) plus a quantity-less phantom carrying the recap
        // grand total 59,11 as a "line" — lines summed to 89,67 €, one item
        // missing, and the visible name was just the PLU. The items must be
        // rebuilt from the printed "qty × unit … total" rows: BOTH items,
        // net totals, PLU as the code, and the nearest text line as the name.
        var text = Fixture("RECEIPT_PRESPOR_145.txt");
        var entities = new[]
        {
            // Live: Document AI grabbed the handwritten site note as supplier.
            new DocumentAiEntity("supplier_name", "KALISOVA'", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "7760332 1.00 x 30.56 C 30.56", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/product_code", "7760332", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/description", "7760332", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "1", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "30.56", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            new DocumentAiEntity("line_item", "Zaokrúhlenie: 59.11", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "Zaokrúhlenie:", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "59.11", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        // The handwritten note is NOT the supplier — the first company-suffix
        // line in the head is; the note is the site (akcia).
        Assert.True(inv.Header.IsReceipt);
        Assert.Equal("PRESPOR spol. s r.o.", inv.Header.SupplierName);
        Assert.Equal(48.06m, inv.Header.TotalExclVat);
        Assert.Equal(11.05m, inv.Header.TotalVat);
        Assert.Equal(59.10m, inv.Header.TotalInclVat);

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal("KALISOVA", dl.AkciaName);    // → Pracovisko auto-match
        Assert.Equal(2, dl.Lines.Count);           // both printed items, not one lump
        var item1 = dl.Lines[0];
        Assert.Equal(24.85m, item1.LineTotalExclVat);
        Assert.Equal("7760332", item1.SupplierItemCode);   // PLU is a code, not a name
        Assert.Equal("PS.FSB6 MONDRILLO BLACK diamant,kor.za s ucha", item1.Description);
        Assert.Equal(1m, item1.Quantity);
        Assert.Equal(10m, item1.DiscountPercent);          // "ZLAVA 10%" above the row
        var item2 = dl.Lines[1];
        Assert.Equal(23.21m, item2.LineTotalExclVat);
        Assert.Equal("CO.Dia vrtak 6,0 PROJAHN M14", item2.Description);
        Assert.Equal(1m, item2.Quantity);          // derived: total ÷ unit (OCR broke the printed qty)
        Assert.Equal(23.21m, item2.UnitPriceExclVat);
        Assert.Equal(10m, item2.DiscountPercent);          // "LAVA 10%" OCR-merged into the row
        Assert.Equal(48.06m, dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m));
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.True(Math.Abs(48.06m + vat - 59.10m) <= 0.05m);
    }

    // ──────────────── Divergent supplier: HEKTRANS single-table layout ────────
    // HEKTRANS s.r.o. single-table invoice (file az_profistav…20260470).
    // V2 added the label alternations from INVOICE_SCANNING_V2_NEW_SUPPLIERS.md
    // (Dátum vydania, Základ DPH / Suma na úhradu, € currency, invoice-number
    // safety net), so the header and totals now parse from the text layer.

    // ────────── V3: DEK text recovery + zero-VAT (reverse-charge) lists ───────
    // Fixes two live failures on re-uploaded invoices (2026-07-08):
    //  A) FA 2600132372 / DL-100-26-014819: Document AI returned no line_item
    //     for a page-bottom row (its cells were reflowed after the printed
    //     subtotal), so the delivery list came back with ZERO lines and the
    //     review under-counted by 51,68 € incl. VAT. Text recovery rebuilds
    //     the row(s) from the segment text and accepts them only when they
    //     reconcile with the printed subtotal to the cent.
    //  B) FA 2600141367 / DL-100-26-015918: same reflow put the price cells
    //     past the printed subtotal (= past rowEnd), so the no-VAT-uplift
    //     check could not fire and a reverse-charge line got 23 % VAT
    //     (phantom 46,58 €). A delivery list whose printed subtotal carries
    //     zero VAT now forces 0 % on all its lines.

    /// <summary>Reconciliation exactly as the review page computes it: sum of
    /// line totals + per-line VAT, rounded per line.</summary>
    private static decimal ClientGrandTotal(ParsedInvoice inv)
    {
        var lines = inv.DeliveryLists.SelectMany(dl => dl.Lines).ToList();
        var excl = lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat  = lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        return Math.Round(excl + vat, 2, MidpointRounding.AwayFromZero);
    }

    // With an EMPTY entity list every delivery list starts with zero lines, so
    // this exercises the text-recovery path for EVERY delivery list on all
    // three DEK fixtures: each must rebuild its lines from the text and
    // reconcile per-DL and per-invoice to the cent.
    [Theory]
    [InlineData("FA_2600132372.txt", 402.37)]
    [InlineData("FA_2600141367.txt", 1788.43)]
    [InlineData("FA_2600150614.txt", 657.16)]
    public void DekInvoices_TextRecovery_RebuildsAllLines_AndReconcilesToPrintedTotal(
        string fixture, double printedInclVat)
    {
        var inv = ParseTextOnly(fixture);

        Assert.All(inv.DeliveryLists, dl => Assert.NotEmpty(dl.Lines));
        Assert.All(inv.DeliveryLists, dl =>
            Assert.Equal(dl.SubtotalExclVat!.Value, dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m), 2));
        Assert.Equal((decimal)printedInclVat, ClientGrandTotal(inv), 2);
    }

    [Fact]
    public void Dek132372_PageBottomRow_DL014819_IsRecoveredFromText()
    {
        var inv = ParseTextOnly("FA_2600132372.txt");
        var dl = Assert.Single(inv.DeliveryLists, d => d.DeliveryNoteRef == "DL-100-26-014819");
        var line = Assert.Single(dl.Lines);
        Assert.Equal("1420099210", line.SupplierItemCode);
        Assert.Contains("Ravatherm 300", line.Description);
        Assert.Equal(42.02m, line.LineTotalExclVat);
        Assert.Equal(23m, line.VatRate);
        Assert.False(line.IsReverseCharge);
    }

    [Fact]
    public void Dek141367_ZeroVatDeliveryList_ForcesReverseCharge_EvenWhenCellsDriftPastSubtotal()
    {
        // Verbatim Document AI text of DL-100-26-015918 from the 2026-07-08
        // re-upload: the numeric cells arrive AFTER the printed subtotal, so
        // the per-row price block is out of the line's search range. The
        // entity mirrors what Document AI actually returned for the row —
        // description + quantity + amount, no product_code, no "**" marker.
        const string text = """
            za dodací list DL-100-26-015918 zo dňa 21.5.2026 | pobočka: Bratislava | spôsob dopravy: Vlastný odvoz z DEK | prevzal: Sroka Vladimír | akcia:
            Alzbetin Dvor | dátum dodania tovaru alebo služby: 21.05.2026
            ** 4400993170 KR KH 20 6x6/150x150/2000x3000
            základ DPH 0% 202,50 EUR | DPH 0% 0,00 EUR |
            12,0000 ks
            27,66 39,00 %
            16,88
            16,88
            202,50
            202,50 EUR
            """;
        var lineItem = new DocumentAiEntity("line_item", "KR KH 20 6x6/150x150/2000x3000", null, 0.9f,
            new[]
            {
                new DocumentAiEntity("line_item/description", "KR KH 20 6x6/150x150/2000x3000", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "12", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "202,50", null, 0.9f, Array.Empty<DocumentAiEntity>())
            });

        var inv = Parser.Parse(new DocumentAiResult("{}", new[] { lineItem }, text));

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal("DL-100-26-015918", dl.DeliveryNoteRef);
        Assert.Equal(202.50m, dl.SubtotalExclVat);
        Assert.Equal(0m, dl.SubtotalVat);
        var line = Assert.Single(dl.Lines);
        Assert.Equal(202.50m, line.LineTotalExclVat);
        Assert.Equal(0m, line.VatRate);        // was 23 % → phantom 46,58 € VAT
        Assert.True(line.IsReverseCharge);
    }

    [Fact]
    public void SingleTableReverseChargeInvoice_ZeroVatHeaderCarry_ForcesReverseChargeLines()
    {
        // KOVOUNI-BA-style layout (FV260492): no "za dodací list" grouping and
        // the whole invoice is §69 reverse charge — printed excl == printed
        // incl, VAT 0,00. The synthetic segment receives its subtotals from
        // the header carry-over; its lines must then be zero-rated, otherwise
        // reconciliation shows phantom 23 % VAT on top of the printed total.
        const string text = """
            Dodávateľ: KOVOUNI-BA s.r.o.
            Faktúra FV260492
            Fakturujeme Vám na základe Vašej objednávky za výrobu a dodanie spracovanej betonárskej ocele.
            99996 betonárska oceľ spracovaná R8 U-cko 540/70/540 - 120KS
            54,510 kg 0,800 43,61
            0% 43,61 43,61
            Prenesenie daňovej povinnosti podľa §69 ods. 12 zákona o DPH - základ dane: 43,61 EUR
            K úhrade 43,61 EUR
            """;
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "FV260492", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            // Live: Document AI returned the CUSTOMER's block as the supplier.
            new DocumentAiEntity("supplier_name", "AZ Profistav, s. r. o.", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("net_amount", "43,61", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("total_amount", "43,61", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "betonárska oceľ spracovaná R8 U-cko", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "betonárska oceľ spracovaná R8 U-cko 540/70/540 - 120KS", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "54,510", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit", "kg", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "43,61", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        // We can never be our own supplier — the dodávateľ line wins.
        Assert.Equal("KOVOUNI-BA s.r.o.", inv.Header.SupplierName);
        Assert.Equal("FV260492", inv.Header.InvoiceNumber);
        Assert.Equal(43.61m, inv.Header.TotalExclVat);
        Assert.Equal(43.61m, inv.Header.TotalInclVat);
        Assert.Equal(0m, inv.Header.TotalVat);

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Null(dl.DeliveryNoteRef);
        Assert.Equal(43.61m, dl.SubtotalExclVat);
        Assert.Equal(0m, dl.SubtotalVat);
        var line = Assert.Single(dl.Lines);
        Assert.Equal(43.61m, line.LineTotalExclVat);
        Assert.Equal(0m, line.VatRate);
        Assert.True(line.IsReverseCharge);
    }

    [Fact]
    public void SingleTableInvoice_LoneLineWithWrongAmount_IsSnappedToPrintedTotal()
    {
        // KOVOUNI-BA photo scan (FV260492, 2026-07-08): Document AI multiplied
        // the quantity by the wrong column and returned 2 377,18 for a 43,61 €
        // invoice, plus a quantity-less phantom row from a description
        // fragment ("7214"). The printed tax base ("základ dane: 43,61 EUR")
        // is authoritative: the lone carrying line is snapped to it.
        const string text = """
            Faktúra FV260492
            Fakturujeme Vám na základe Vašej objednávky za výrobu a dodanie spracovanej betonárskej ocele.
            99996 betonárska oceľ spracovaná R8 U-cko 540/70/540 - 120KS
            54,510 kg 0,800 43,61
            7214
            0% 43,61 43,61
            Prenesenie daňovej povinnosti podľa §69 ods. 12 zákona o DPH - základ dane: 43,61 EUR
            K úhrade 43,61 EUR
            """;
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "FV260492", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("total_amount", "43,61", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "betonárska oceľ spracovaná R8 U-cko", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "betonárska oceľ spracovaná R8 U-cko 540/70/540 - 120KS", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "54,510", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "2 377,18", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            new DocumentAiEntity("line_item", "7214", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/product_code", "7214", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        // "základ dane: 43,61 EUR" is the excl total on §69 invoices.
        Assert.Equal(43.61m, inv.Header.TotalExclVat);
        Assert.Equal(0m, inv.Header.TotalVat);

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(43.61m, dl.SubtotalExclVat);
        var carrying = dl.Lines.Single(l => (l.LineTotalExclVat ?? 0m) != 0m);
        Assert.Equal(43.61m, carrying.LineTotalExclVat);   // was 2 377,18
        Assert.Equal(0m, carrying.VatRate);
        // Reconciliation as the review page computes it.
        var excl = dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(43.61m, excl + vat);
    }

    [Fact]
    public void SingleTableInvoice_LoneRealLineWithNoAmountAtAll_TakesThePrintedTotal()
    {
        // Second KOVOUNI-BA re-scan (2026-07-08, upright PDF): Document AI
        // returned NO amount property at all — only a unit_price carrying the
        // mismapped 43,61 column — so the line total came out null and the
        // review under-counted to zero. The lone real line (the only one with
        // a quantity) takes the printed subtotal, and its unit price is
        // re-derived from it (43,61 / 54,510 kg = 0,80).
        const string text = """
            Faktúra FV260492
            Fakturujeme Vám na základe Vašej objednávky za výrobu a dodanie spracovanej betonárskej ocele.
            99996 betonárska oceľ spracovaná R8 U-cko 540/70/540 - 120KS
            54,510 kg 0,800 43,61
            7214
            0% 43,61 43,61
            Prenesenie daňovej povinnosti podľa §69 ods. 12 zákona o DPH - základ dane: 43,61 EUR
            K úhrade 43,61 EUR
            """;
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "FV260492", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("total_amount", "43,61", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "99996 betonárska oceľ spracovaná R8 U-cko", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/product_code", "99996", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/description", "betonárska oceľ spracovaná R8 U-cko 540/70/540-120KS", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "54,510", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit", "kg", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit_price", "43,61", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            new DocumentAiEntity("line_item", "7214", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/product_code", "7214", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(43.61m, dl.SubtotalExclVat);
        Assert.Equal(0m, dl.SubtotalVat);
        var carrying = dl.Lines.Single(l => (l.Quantity ?? 0m) != 0m);
        Assert.Equal(43.61m, carrying.LineTotalExclVat);
        Assert.Equal(0.80m, carrying.UnitPriceExclVat!.Value, 2);
        Assert.Equal(0m, carrying.VatRate);
        var excl = dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(43.61m, excl + vat);
    }

    [Fact]
    public void AzStav_RealScrambledScan_TotalsFromVatSummaryWindow_AndLoneLineReconciles()
    {
        // Verbatim text layer of the rejected A-Z STAV upload (2026-07-08).
        // The summary block reflows as label lines followed by a value run
        // ("Základná sadzba DPH 23% / Celkom: / Daň % / 23 / Cena bez DPH /
        // 249,12 / DPH Cena s DPH / 57,30 / 249,12 / 57,30 / 306,42 EUR /
        // 306,42 EUR") — a fixed row-shape regex cannot match it. Document AI
        // supplied only description/quantity/unit for the line, so the totals
        // must come from the window read and the line takes the printed base.
        var text = Fixture("AZSTAV_26200942.txt");
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "26200942", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "Recyklované kamenivo hrubé\n(betónový recyklát) 10/22 27,680 tona : OFO/2026/871", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "Recyklované kamenivo hrubé\n(betónový recyklát) 10/22", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "27,680", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit", "tona", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        Assert.Equal("26200942", inv.Header.InvoiceNumber);
        // The dodávateľ block's suffix was OCR'd as "s.г.0." (Cyrillic г,
        // digit zero) — the loosened suffix matcher finds it and normalises.
        Assert.Equal("A-Z STAV, s.r.o.", inv.Header.SupplierName);
        Assert.Equal(249.12m, inv.Header.TotalExclVat);
        Assert.Equal(57.30m, inv.Header.TotalVat);
        Assert.Equal(306.42m, inv.Header.TotalInclVat);   // upload gate needs this

        // Scrambled label/value runs: "Dátum vystavenia: / Dátum dodania: /
        // 12.5.2026 / 9.5.2026 / 26.5.2026 … Dátum splatnosti:" — paired by
        // order. Without this the invoice gets stamped with the UPLOAD date
        // and lands in the wrong month on the Financie overview.
        Assert.Equal(new DateTime(2026, 5, 12), inv.Header.IssueDate);
        Assert.Equal(new DateTime(2026, 5, 9), inv.Header.DeliveryDate);
        Assert.Equal(new DateTime(2026, 5, 26), inv.Header.DueDate);

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(249.12m, dl.SubtotalExclVat);
        Assert.Equal(57.30m, dl.SubtotalVat);
        var line = Assert.Single(dl.Lines);
        Assert.Equal(249.12m, line.LineTotalExclVat);
        Assert.Equal(9.00m, line.UnitPriceExclVat!.Value, 2);
        Assert.Equal(23m, line.VatRate);
        var excl = dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(306.42m, excl + vat);
    }

    [Fact]
    public void AzStav_FragmentedEntities_QuantityOnlyFragmentStillBecomesTheLine()
    {
        // A second scan of the same A-Z STAV photo (2026-07-08) fragmented the
        // row into three line_items; the first carries ONLY quantity+unit
        // ("27,680 tona") — no description property. Offset anchoring used to
        // require a description, so every fragment was dropped and the review
        // showed 0,00 € against a printed 306,42 €. The mention text now
        // anchors such fragments, the name fragment (whose text sits BEFORE
        // the quantity fragment — the backward retry finds it) merges into
        // the quantity line, and the line takes the printed base.
        var text = Fixture("AZSTAV_26200942.txt");
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "26200942", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "27,680 tona", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/quantity", "27,680", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit", "tona", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            new DocumentAiEntity("line_item", "Recyklované kamenivo hrubé\n(betónový recyklát) 10/22", null, 0.9f,
                Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "Základná sadzba DPH 23%", null, 0.9f,
                Array.Empty<DocumentAiEntity>())
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        // Totals: pairing inside the summary window (base + VAT = incl).
        Assert.Equal(249.12m, inv.Header.TotalExclVat);
        Assert.Equal(57.30m, inv.Header.TotalVat);
        Assert.Equal(306.42m, inv.Header.TotalInclVat);

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(249.12m, dl.SubtotalExclVat);
        Assert.Equal(57.30m, dl.SubtotalVat);
        var carrying = Assert.Single(dl.Lines);    // fragments merged into ONE named line
        Assert.Equal("Recyklované kamenivo hrubé (betónový recyklát) 10/22", carrying.Description);
        Assert.Equal(27.680m, carrying.Quantity);
        Assert.Equal(249.12m, carrying.LineTotalExclVat);
        Assert.Equal(23m, carrying.VatRate);
        var excl = dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(306.42m, excl + vat);
    }

    [Fact]
    public void AzStav_OnlyNumericEntities_NameComesFromTextAboveTheAnchor()
    {
        // Third scan of the A-Z STAV photo (2026-07-13, live id 74): Document
        // AI returned ONLY numeric fragments — quantity, a unit_price and an
        // amount — no name entity at all, so the review showed "27,680 tona"
        // as the item name. The name must be read from the text block above
        // the line's anchor, between the table header words.
        var text = Fixture("AZSTAV_26200942.txt");
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "26200942", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "27,680 tona", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/quantity", "27,680", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit", "tona", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            new DocumentAiEntity("line_item", "249,12", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/unit_price", "249,12", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            new DocumentAiEntity("line_item", "306,42", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/amount", "306,42", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        var dl = Assert.Single(inv.DeliveryLists);
        var line = Assert.Single(dl.Lines);
        Assert.Equal("Recyklované kamenivo hrubé (betónový recyklát) 10/22", line.Description);
        Assert.Equal(27.680m, line.Quantity);
        Assert.Equal(249.12m, line.LineTotalExclVat);
    }

    [Fact]
    public void VatSummaryRow_SuppliesAllThreeTotals_AndLoneLineReconciles()
    {
        // A-Z STAV 26200942 (photo scan, 2026-07-08): Document AI returned no
        // total_amount entity and no amounts on the line — only description,
        // quantity and unit — so the upload was rejected for a missing grand
        // total. The only EUR-anchored totals on this layout are in the
        // per-rate summary row "Základná sadzba DPH 23% 23 249,12 57,30
        // 306,42EUR": it must fill base, VAT and incl-total, and the lone
        // real line then takes the printed base (unit price 249,12 / 27,680 t
        // = 9,00).
        const string text = """
            Faktúra
            26200942
            Dodávateľ:
            A-Z STAV, s.r.o.
            Odberateľ
            AZ Profistav, s. r. o.
            Popis: Odpad 02.05. - 09.05.2026
            Tovar Množstvo Bez DPH/MJ Daň % Cena bez DPH DPH Cena s DPH
            Recyklované kamenivo hrubé
            (betónový recyklát) 10/22 27,680 tona 9,00 23 249,12 57,30 306,42 EUR
            Základná sadzba DPH 23% 23 249,12 57,30 306,42EUR
            Celkom:
            """;
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "26200942", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "Recyklované kamenivo hrubé\n(betónový recyklát) 10/22 27,680 tona", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/description", "Recyklované kamenivo hrubé\n(betónový recyklát) 10/22", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "27,680", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit", "tona", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        Assert.Equal("26200942", inv.Header.InvoiceNumber);
        Assert.Equal(249.12m, inv.Header.TotalExclVat);
        Assert.Equal(57.30m, inv.Header.TotalVat);
        Assert.Equal(306.42m, inv.Header.TotalInclVat);   // upload gate needs this

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(249.12m, dl.SubtotalExclVat);
        Assert.Equal(57.30m, dl.SubtotalVat);
        var line = Assert.Single(dl.Lines);
        Assert.Equal(249.12m, line.LineTotalExclVat);
        Assert.Equal(9.00m, line.UnitPriceExclVat!.Value, 2);
        Assert.Equal(23m, line.VatRate);
        var excl = dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(306.42m, excl + vat);
    }

    // ─────────────────────────── Hilti Slovakia layout ────────────────────────

    [Fact]
    public void Hilti_ItemNetAndHandlingFee_ParseAndReconcile()
    {
        // Real capture of Faktúra 1841020753 (photo scan, 2026-07-08). Two
        // quirks: Document AI's line amount is the PRE-discount 28,59 (the
        // post-discount 25,16 sits under "Netto hodnota položky"), and the
        // 16,00 "Manipulačný poplatok" appears only in the summary block,
        // whose labels and values OCR as two separate runs of lines.
        var text = Fixture("HILTI_1841020753.txt");
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "1841020753", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("supplier_name", "Hilti Slovakia S.R.O.", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("total_amount", "50,63", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "Set lamel pre DGH 130", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/product_code", "2200273", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/description", "Set lamel pre DGH 130", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/quantity", "3", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit_price", "9,53", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "28,59", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            // Phantom row Document AI emitted from stray table text.
            new DocumentAiEntity("line_item", "Spolu", null, 0.9f, Array.Empty<DocumentAiEntity>())
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        Assert.Equal("1841020753", inv.Header.InvoiceNumber);
        Assert.False(inv.Header.IsReceipt);                 // proper invoice, no eKasa markers
        Assert.Equal(50.63m, inv.Header.TotalInclVat);
        Assert.Equal("31344445", inv.Header.SupplierIco);   // footer block, not the customer's

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(41.16m, dl.SubtotalExclVat);
        Assert.Equal(9.47m, dl.SubtotalVat);

        Assert.Equal(2, dl.Lines.Count);
        var item = dl.Lines[0];
        Assert.Equal("2200273", item.SupplierItemCode);
        Assert.Equal(25.16m, item.LineTotalExclVat);        // not the pre-discount 28,59
        Assert.Equal(3m, item.Quantity);
        Assert.Equal(23m, item.VatRate);
        var poplatok = dl.Lines[1];
        Assert.Equal("Manipulačný poplatok", poplatok.Description);
        Assert.Equal(16.00m, poplatok.LineTotalExclVat);
        Assert.True(poplatok.IsService);

        // Reconciliation as the review page computes it: 25,16 + 16,00 = 41,16
        // excl; VAT 5,79 + 3,68 = 9,47; grand 50,63 == printed.
        var excl = dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(50.63m, excl + vat);
    }

    [Fact]
    public void Hilti_RowSplitIntoTwoEntities_IsMergedAndKeepsItsName()
    {
        // The 2026-07-08 re-scan of 1841020753: Document AI split the single
        // item row into a code/description entity (no quantity) and a
        // quantity/price entity (no name). Unmerged, both halves were dropped
        // as phantoms and the handling fee silently absorbed the whole
        // subtotal — totals looked right but the material had no name.
        var text = Fixture("HILTI_1841020753.txt");
        var entities = new[]
        {
            new DocumentAiEntity("invoice_id", "1841020753", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("supplier_name", "Hilti Slovakia S.R.O.", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("total_amount", "50,63", null, 0.9f, Array.Empty<DocumentAiEntity>()),
            new DocumentAiEntity("line_item", "2200273 Set lamel pre DGH 130\nSpoločný colný sadzobník: 40169300", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/product_code", "2200273", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/description", "Set lamel pre DGH 130\nSpoločný colný sadzobník: 40169300", null, 0.9f, Array.Empty<DocumentAiEntity>())
            }),
            new DocumentAiEntity("line_item", "3 KS 9,53 28,59", null, 0.9f, new[]
            {
                new DocumentAiEntity("line_item/quantity", "3", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit", "KS", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/unit_price", "9,53", null, 0.9f, Array.Empty<DocumentAiEntity>()),
                new DocumentAiEntity("line_item/amount", "28,59", null, 0.9f, Array.Empty<DocumentAiEntity>())
            })
        };

        var inv = Parser.Parse(new DocumentAiResult("{}", entities, text));

        var dl = Assert.Single(inv.DeliveryLists);
        Assert.Equal(2, dl.Lines.Count);
        var item = dl.Lines[0];
        Assert.Equal("2200273", item.SupplierItemCode);
        Assert.Equal("Set lamel pre DGH 130", item.Description);   // named, customs footnote stripped
        Assert.Equal(3m, item.Quantity);
        Assert.Equal(25.16m, item.LineTotalExclVat);
        Assert.Equal(16.00m, dl.Lines[1].LineTotalExclVat);        // fee stays 16,00 — no snap-absorption
        var excl = dl.Lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat = dl.Lines.Sum(l => Math.Round((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(50.63m, excl + vat);
    }

    [Fact]
    public void Hektrans_SupplierIdentity_StillParses_FromGenericPatterns()
    {
        var inv = ParseTextOnly("HEKTRANS_20260470.txt");
        Assert.Equal("36055140", inv.Header.SupplierIco);
        Assert.Equal("SK2020072648", inv.Header.SupplierIcDph);
        Assert.Equal("SK5902000000003491663653", inv.Header.SupplierIban); // "IBAN:" label, spaces stripped
        Assert.Contains("HEKTRANS", inv.Header.SupplierName ?? "");
        Assert.Equal(new DateTime(2026, 6, 15), inv.Header.DueDate); // "dátum splatnosti" matches even here
    }

    [Fact]
    public void Hektrans_HeaderAndTotals_ParseFromTextLayer()
    {
        var inv = ParseTextOnly("HEKTRANS_20260470.txt");

        // No "za dodací list": parser falls back to ONE synthetic segment.
        Assert.Single(inv.DeliveryLists);
        Assert.Null(inv.DeliveryLists[0].DeliveryNoteRef);
        Assert.Null(inv.DeliveryLists[0].AkciaName); // no worksite -> auto-match would pick Sklad

        // V2 label support (these were all null before the fix):
        Assert.Equal("20260470", inv.Header.InvoiceNumber);          // safety net: first number near "Faktúra"
        Assert.Equal(new DateTime(2026, 5, 31), inv.Header.IssueDate); // "Dátum vydania"
        Assert.Equal(300.00m, inv.Header.TotalExclVat);              // "Základ DPH … €" summary box
        Assert.Equal(369.00m, inv.Header.TotalInclVat);              // "Suma na úhradu … €"

        // With no per-DL subtotal, the synthetic segment carries the header
        // summary-box totals so reconciliation has a basis.
        Assert.Equal(300.00m, inv.DeliveryLists[0].SubtotalExclVat);
        Assert.Equal(69.00m, inv.DeliveryLists[0].SubtotalVat);
    }
}
