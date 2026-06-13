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

    // ──────────────── Divergent supplier: HEKTRANS single-table layout ────────
    // HEKTRANS s.r.o. single-table invoice (file az_profistav…20260470).
    // V2 added the label alternations from INVOICE_SCANNING_V2_NEW_SUPPLIERS.md
    // (Dátum vydania, Základ DPH / Suma na úhradu, € currency, invoice-number
    // safety net), so the header and totals now parse from the text layer.

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
