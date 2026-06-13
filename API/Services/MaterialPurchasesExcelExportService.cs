using ClosedXML.Excel;
using API.DTOs;

namespace API.Services;

/// <summary>
/// Builds the .xlsx report for the admin Materiál → Nákupy tab.
/// Mirrors the shape of <see cref="IMaterialExcelExportService"/> (Súhrn + Detailný záznam),
/// but groups across all sites by default — the customer filters via query params before exporting.
/// </summary>
public interface IMaterialPurchasesExcelExportService
{
    byte[] BuildPurchasesReport(
        DateTime? from,
        DateTime? to,
        string? locationName,
        string? employeeName,
        string? supplierFilter,
        IEnumerable<MaterialPurchaseDto> purchases);
}

public class MaterialPurchasesExcelExportService : IMaterialPurchasesExcelExportService
{
    private const string EurFormat = "#,##0.00 €";

    public byte[] BuildPurchasesReport(
        DateTime? from,
        DateTime? to,
        string? locationName,
        string? employeeName,
        string? supplierFilter,
        IEnumerable<MaterialPurchaseDto> purchases)
    {
        var purchaseList = purchases.OrderByDescending(p => p.PurchaseDate).ToList();

        // Flatten lines across all purchases for the per-material summary + detail sheet.
        // Group by (MaterialNameRaw lower-cased, Unit lower-cased) so the same material
        // shows up as one row even if some lines are still "neidentifikovaný" (MaterialId null)
        // and others have been promoted — the raw name is the stable key.
        var allLines = purchaseList
            .SelectMany(p => p.Lines.Select(l => new { Purchase = p, Line = l }))
            .ToList();

        using var wb = new XLWorkbook();

        // ────── Sheet 1: Súhrn po materiáli ──────
        var s1 = wb.Worksheets.Add("Súhrn");

        s1.Cell(1, 1).Value = "Nákupy materiálu — súhrn";
        s1.Range(1, 1, 1, 6).Merge();
        s1.Cell(1, 1).Style.Font.Bold = true;
        s1.Cell(1, 1).Style.Font.FontSize = 14;

        var rangeText = (from.HasValue || to.HasValue)
            ? $"Obdobie: {(from?.ToString("dd.MM.yyyy") ?? "začiatok")} – {(to?.ToString("dd.MM.yyyy") ?? "dnes")}"
            : "Obdobie: celé obdobie";
        s1.Cell(2, 1).Value = rangeText;
        s1.Range(2, 1, 2, 6).Merge();
        s1.Cell(2, 1).Style.Font.Italic = true;

        var filterParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(locationName))  filterParts.Add($"Pracovisko: {locationName}");
        if (!string.IsNullOrWhiteSpace(employeeName))  filterParts.Add($"Zamestnanec: {employeeName}");
        if (!string.IsNullOrWhiteSpace(supplierFilter)) filterParts.Add($"Dodávateľ: {supplierFilter}");
        if (filterParts.Count > 0)
        {
            s1.Cell(3, 1).Value = string.Join("   |   ", filterParts);
            s1.Range(3, 1, 3, 6).Merge();
            s1.Cell(3, 1).Style.Font.Italic = true;
        }

        // Header row at row 5 (leaves a blank row above for breathing room — matches the consumption sheet).
        s1.Cell(5, 1).Value = "Materiál";
        s1.Cell(5, 2).Value = "Jednotka";
        s1.Cell(5, 3).Value = "Spolu množstvo";
        s1.Cell(5, 4).Value = "Spolu náklady";
        s1.Cell(5, 5).Value = "Priem. cena";
        s1.Cell(5, 6).Value = "Počet riadkov";
        var s1Header = s1.Range(5, 1, 5, 6);
        s1Header.Style.Font.Bold = true;
        s1Header.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBBF24");
        s1Header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var summary = allLines
            .GroupBy(x => new
            {
                NameKey = (x.Line.MaterialNameRaw ?? string.Empty).Trim().ToLowerInvariant(),
                UnitKey = (x.Line.Unit ?? string.Empty).Trim().ToLowerInvariant()
            })
            .Select(g => new
            {
                // Display name: pick the first non-empty raw name in the group's chronological order.
                Name = g.OrderBy(x => x.Purchase.PurchaseDate).First().Line.MaterialNameRaw ?? string.Empty,
                Unit = g.OrderBy(x => x.Purchase.PurchaseDate).First().Line.Unit ?? string.Empty,
                TotalQty = g.Sum(x => x.Line.Quantity),
                TotalCost = g.Sum(x => x.Line.LineTotal),
                LineCount = g.Count(),
                // Volume-weighted avg unit price = sum(cost) / sum(qty); guard divide-by-zero.
                AvgPrice = g.Sum(x => x.Line.Quantity) == 0m
                    ? 0m
                    : g.Sum(x => x.Line.LineTotal) / g.Sum(x => x.Line.Quantity)
            })
            .OrderByDescending(s => s.TotalCost)
            .ToList();

        var row = 6;
        foreach (var r in summary)
        {
            s1.Cell(row, 1).Value = r.Name;
            s1.Cell(row, 2).Value = r.Unit;
            s1.Cell(row, 3).Value = (double)r.TotalQty;

            var costCell = s1.Cell(row, 4);
            costCell.Value = (double)r.TotalCost;
            costCell.Style.NumberFormat.Format = EurFormat;

            var avgCell = s1.Cell(row, 5);
            avgCell.Value = (double)r.AvgPrice;
            avgCell.Style.NumberFormat.Format = EurFormat;

            s1.Cell(row, 6).Value = r.LineCount;
            row++;
        }

        if (summary.Count > 0)
        {
            s1.Cell(row, 1).Value = "Spolu:";
            s1.Cell(row, 1).Style.Font.Bold = true;

            var grandCost = s1.Cell(row, 4);
            grandCost.Value = (double)summary.Sum(r => r.TotalCost);
            grandCost.Style.NumberFormat.Format = EurFormat;
            grandCost.Style.Font.Bold = true;

            s1.Cell(row, 6).Value = summary.Sum(r => r.LineCount);
            s1.Cell(row, 6).Style.Font.Bold = true;

            s1.Range(row, 1, row, 6).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        s1.SheetView.FreezeRows(5);
        s1.Columns().AdjustToContents();

        // ────── Sheet 2: Nákupy (one row per receipt) ──────
        // Per-purchase view — quick scan of who bought what for which site, total
        // cost, and a one-click "Otvoriť účtenku" hyperlink + the raw URL so the
        // accountant can copy/paste or save the receipt directly. Added 2026-05-06.
        var sP = wb.Worksheets.Add("Nákupy");

        sP.Cell(1, 1).Value = "Dátum";
        sP.Cell(1, 2).Value = "Zamestnanec";
        sP.Cell(1, 3).Value = "Pracovisko";
        sP.Cell(1, 4).Value = "Dodávateľ";
        sP.Cell(1, 5).Value = "Položiek";
        sP.Cell(1, 6).Value = "Spolu";
        sP.Cell(1, 7).Value = "Účtenka";
        sP.Cell(1, 8).Value = "URL účtenky";
        sP.Cell(1, 9).Value = "Poznámka";
        var sPHeader = sP.Range(1, 1, 1, 9);
        sPHeader.Style.Font.Bold = true;
        sPHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBBF24");
        sPHeader.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var pRow = 2;
        foreach (var p in purchaseList)
        {
            sP.Cell(pRow, 1).Value = p.PurchaseDate;
            sP.Cell(pRow, 1).Style.DateFormat.Format = "dd.MM.yyyy";
            sP.Cell(pRow, 2).Value = p.EmployeeName;
            sP.Cell(pRow, 3).Value = p.LocationName ?? "Inventár";
            sP.Cell(pRow, 4).Value = p.SupplierName ?? "";
            sP.Cell(pRow, 5).Value = p.Lines.Count;

            var totalCell = sP.Cell(pRow, 6);
            totalCell.Value = (double)p.TotalCost;
            totalCell.Style.NumberFormat.Format = EurFormat;

            if (!string.IsNullOrEmpty(p.ReceiptPhotoUrl))
            {
                // Quick-download hyperlink — clicking opens the receipt in the browser.
                var openCell = sP.Cell(pRow, 7);
                openCell.Value = "Otvoriť";
                openCell.SetHyperlink(new XLHyperlink(p.ReceiptPhotoUrl));
                openCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                openCell.Style.Font.Underline = XLFontUnderlineValues.Single;

                // Raw URL — for copy/paste into other tools or scripts.
                sP.Cell(pRow, 8).Value = p.ReceiptPhotoUrl;
                sP.Cell(pRow, 8).Style.Font.FontColor = XLColor.FromHtml("#6B7280");
                sP.Cell(pRow, 8).Style.Font.FontSize = 10;
            }
            else
            {
                sP.Cell(pRow, 7).Value = "—";
                sP.Cell(pRow, 7).Style.Font.FontColor = XLColor.FromHtml("#9CA3AF");
            }

            sP.Cell(pRow, 9).Value = p.Note ?? "";
            pRow++;
        }

        if (purchaseList.Count > 0)
        {
            sP.Cell(pRow, 1).Value = "Spolu:";
            sP.Cell(pRow, 1).Style.Font.Bold = true;

            sP.Cell(pRow, 5).Value = purchaseList.Sum(p => p.Lines.Count);
            sP.Cell(pRow, 5).Style.Font.Bold = true;

            var grand = sP.Cell(pRow, 6);
            grand.Value = (double)purchaseList.Sum(p => p.TotalCost);
            grand.Style.NumberFormat.Format = EurFormat;
            grand.Style.Font.Bold = true;

            sP.Range(pRow, 1, pRow, 9).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        sP.SheetView.FreezeRows(1);
        sP.Columns().AdjustToContents();
        // Date column width — AdjustToContents underestimates dd.MM.yyyy formatting
        // and Excel renders narrow date cells as "###". Force a comfortable width.
        sP.Column(1).Width = Math.Max(sP.Column(1).Width, 14);
        // Cap URL column width — Cloudinary URLs are long and would otherwise dominate the layout.
        sP.Column(8).Width = Math.Min(sP.Column(8).Width, 60);

        // ────── Sheet 3: Detailný záznam (per-line, kept for accountants) ──────
        var s2 = wb.Worksheets.Add("Detailný záznam");

        s2.Cell(1, 1).Value = "Dátum";
        s2.Cell(1, 2).Value = "Zamestnanec";
        s2.Cell(1, 3).Value = "Pracovisko";
        s2.Cell(1, 4).Value = "Dodávateľ";
        s2.Cell(1, 5).Value = "Materiál";
        s2.Cell(1, 6).Value = "Množstvo";
        s2.Cell(1, 7).Value = "Jednotka";
        s2.Cell(1, 8).Value = "Cena/jednotka";
        s2.Cell(1, 9).Value = "Náklady";
        s2.Cell(1, 10).Value = "Poznámka";
        s2.Cell(1, 11).Value = "Účtenka";
        var s2Header = s2.Range(1, 1, 1, 11);
        s2Header.Style.Font.Bold = true;
        s2Header.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBBF24");
        s2Header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        row = 2;
        foreach (var p in purchaseList)
        {
            foreach (var l in p.Lines)
            {
                s2.Cell(row, 1).Value = p.PurchaseDate;
                s2.Cell(row, 1).Style.DateFormat.Format = "dd.MM.yyyy";
                s2.Cell(row, 2).Value = p.EmployeeName;
                s2.Cell(row, 3).Value = p.LocationName ?? "Inventár";
                s2.Cell(row, 4).Value = p.SupplierName ?? "";
                // Use the raw name — survives admin renames and matches what the worker entered.
                s2.Cell(row, 5).Value = l.MaterialNameRaw;
                s2.Cell(row, 6).Value = (double)l.Quantity;
                s2.Cell(row, 7).Value = l.Unit;

                var unitCell = s2.Cell(row, 8);
                unitCell.Value = (double)l.UnitPrice;
                unitCell.Style.NumberFormat.Format = EurFormat;

                var lineCell = s2.Cell(row, 9);
                lineCell.Value = (double)l.LineTotal;
                lineCell.Style.NumberFormat.Format = EurFormat;

                s2.Cell(row, 10).Value = p.Note ?? "";

                if (!string.IsNullOrEmpty(p.ReceiptPhotoUrl))
                {
                    var c = s2.Cell(row, 11);
                    c.Value = "Otvoriť";
                    c.SetHyperlink(new XLHyperlink(p.ReceiptPhotoUrl));
                    c.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                    c.Style.Font.Underline = XLFontUnderlineValues.Single;
                }
                row++;
            }
        }

        if (purchaseList.Count > 0)
        {
            s2.Cell(row, 1).Value = "Spolu:";
            s2.Cell(row, 1).Style.Font.Bold = true;

            var detailGrand = s2.Cell(row, 9);
            detailGrand.Value = (double)purchaseList.SelectMany(p => p.Lines).Sum(l => l.LineTotal);
            detailGrand.Style.NumberFormat.Format = EurFormat;
            detailGrand.Style.Font.Bold = true;

            s2.Range(row, 1, row, 11).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        s2.SheetView.FreezeRows(1);
        s2.Columns().AdjustToContents();
        // Same date-column width fix as the Nákupy sheet — auto-fit underestimates dd.MM.yyyy.
        s2.Column(1).Width = Math.Max(s2.Column(1).Width, 14);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
