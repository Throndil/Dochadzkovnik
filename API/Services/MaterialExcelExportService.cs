using ClosedXML.Excel;
using API.DTOs;

namespace API.Services;

public interface IMaterialExcelExportService
{
    /// <summary>
    /// Generates a two-sheet workbook (Súhrn + Detailný záznam) for the given location's
    /// material usage in the supplied date range. Returns a ready-to-stream byte array.
    /// </summary>
    byte[] BuildLocationMaterialReport(
        string locationName,
        DateTime? from,
        DateTime? to,
        IEnumerable<MaterialSummaryRowDto> summary,
        IEnumerable<MaterialUsageDto> entries);

    /// <summary>
    /// Cross-Pracoviská workbook covering every active location's material usage.
    /// Sheet 1 = Pracovisko-level totals; Sheet 2 = every entry with a Pracovisko
    /// column. Costs come straight from <see cref="MaterialUsageDto.LineCost"/>,
    /// which carries the snapshotted UnitPriceAtTime — current catalogue price
    /// changes do NOT rewrite the report.
    /// </summary>
    byte[] BuildAllLocationsMaterialReport(
        DateTime? from,
        DateTime? to,
        IEnumerable<(string LocationName, IEnumerable<MaterialUsageDto> Entries)> perLocation);
}

public class MaterialExcelExportService : IMaterialExcelExportService
{
    // EUR with 2 decimal places — Excel format string used for monetary cells.
    private const string EurFormat = "#,##0.00 €";

    public byte[] BuildLocationMaterialReport(
        string locationName,
        DateTime? from,
        DateTime? to,
        IEnumerable<MaterialSummaryRowDto> summary,
        IEnumerable<MaterialUsageDto> entries)
    {
        using var wb = new XLWorkbook();

        // ────── Sheet 1: Súhrn ──────
        var s1 = wb.Worksheets.Add("Súhrn");

        s1.Cell(1, 1).Value = "Spotreba materiálu — " + locationName;
        s1.Range(1, 1, 1, 5).Merge();
        s1.Cell(1, 1).Style.Font.Bold = true;
        s1.Cell(1, 1).Style.Font.FontSize = 14;

        var rangeText = (from.HasValue || to.HasValue)
            ? $"Obdobie: {(from?.ToString("dd.MM.yyyy") ?? "začiatok")} – {(to?.ToString("dd.MM.yyyy") ?? "dnes")}"
            : "Obdobie: celé obdobie";
        s1.Cell(2, 1).Value = rangeText;
        s1.Range(2, 1, 2, 5).Merge();
        s1.Cell(2, 1).Style.Font.Italic = true;

        // Header row
        s1.Cell(4, 1).Value = "Materiál";
        s1.Cell(4, 2).Value = "Jednotka";
        s1.Cell(4, 3).Value = "Spolu množstvo";
        s1.Cell(4, 4).Value = "Spolu náklady";
        s1.Cell(4, 5).Value = "Počet záznamov";
        var s1Header = s1.Range(4, 1, 4, 5);
        s1Header.Style.Font.Bold = true;
        s1Header.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBBF24"); // amber-400
        s1Header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var row = 5;
        var summaryList = summary.ToList();
        foreach (var r in summaryList)
        {
            s1.Cell(row, 1).Value = r.MaterialName;
            s1.Cell(row, 2).Value = r.Unit;
            s1.Cell(row, 3).Value = (double)r.TotalQuantity;
            var costCell = s1.Cell(row, 4);
            costCell.Value = (double)r.TotalCost;
            costCell.Style.NumberFormat.Format = EurFormat;
            s1.Cell(row, 5).Value = r.EntryCount;
            row++;
        }

        // Totals row — sums the cost column (the only sensible cross-material aggregate;
        // total quantities don't make sense across different units).
        if (summaryList.Count > 0)
        {
            s1.Cell(row, 1).Value = "Spolu:";
            s1.Cell(row, 1).Style.Font.Bold = true;

            var grandCost = s1.Cell(row, 4);
            grandCost.Value = (double)summaryList.Sum(r => r.TotalCost);
            grandCost.Style.NumberFormat.Format = EurFormat;
            grandCost.Style.Font.Bold = true;

            s1.Cell(row, 5).Value = summaryList.Sum(r => r.EntryCount);
            s1.Cell(row, 5).Style.Font.Bold = true;

            s1.Range(row, 1, row, 5).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        s1.SheetView.FreezeRows(4);
        s1.Columns().AdjustToContents();

        // ────── Sheet 2: Detailný záznam ──────
        var s2 = wb.Worksheets.Add("Detailný záznam");

        s2.Cell(1, 1).Value = "Dátum";
        s2.Cell(1, 2).Value = "Materiál";
        s2.Cell(1, 3).Value = "Množstvo";
        s2.Cell(1, 4).Value = "Jednotka";
        s2.Cell(1, 5).Value = "Cena/jednotka";
        s2.Cell(1, 6).Value = "Náklady";
        s2.Cell(1, 7).Value = "Zamestnanec";
        s2.Cell(1, 8).Value = "Poznámka";
        s2.Cell(1, 9).Value = "Foto";
        var s2Header = s2.Range(1, 1, 1, 9);
        s2Header.Style.Font.Bold = true;
        s2Header.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBBF24");
        s2Header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        row = 2;
        var entriesList = entries.OrderByDescending(x => x.Date).ToList();
        foreach (var e in entriesList)
        {
            s2.Cell(row, 1).Value = e.Date;
            s2.Cell(row, 1).Style.DateFormat.Format = "dd.MM.yyyy";
            s2.Cell(row, 2).Value = e.MaterialName;
            s2.Cell(row, 3).Value = (double)e.Quantity;
            s2.Cell(row, 4).Value = e.Unit;

            var unitCell = s2.Cell(row, 5);
            unitCell.Value = (double)e.UnitPriceAtTime;
            unitCell.Style.NumberFormat.Format = EurFormat;

            var lineCell = s2.Cell(row, 6);
            lineCell.Value = (double)e.LineCost;
            lineCell.Style.NumberFormat.Format = EurFormat;

            s2.Cell(row, 7).Value = e.EmployeeName ?? "";
            s2.Cell(row, 8).Value = e.Note ?? "";
            if (!string.IsNullOrEmpty(e.PhotoUrl))
            {
                var c = s2.Cell(row, 9);
                c.Value = "Otvoriť";
                c.SetHyperlink(new XLHyperlink(e.PhotoUrl));
                c.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                c.Style.Font.Underline = XLFontUnderlineValues.Single;
            }
            row++;
        }

        // Detail-sheet grand-total row (just the cost column)
        if (entriesList.Count > 0)
        {
            s2.Cell(row, 1).Value = "Spolu:";
            s2.Cell(row, 1).Style.Font.Bold = true;
            var detailGrand = s2.Cell(row, 6);
            detailGrand.Value = (double)entriesList.Sum(e => e.LineCost);
            detailGrand.Style.NumberFormat.Format = EurFormat;
            detailGrand.Style.Font.Bold = true;
            s2.Range(row, 1, row, 9).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        s2.SheetView.FreezeRows(1);
        s2.Columns().AdjustToContents();
        // Force the date column wide enough — AdjustToContents underestimates dd.MM.yyyy.
        s2.Column(1).Width = Math.Max(s2.Column(1).Width, 14);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public byte[] BuildAllLocationsMaterialReport(
        DateTime? from,
        DateTime? to,
        IEnumerable<(string LocationName, IEnumerable<MaterialUsageDto> Entries)> perLocation)
    {
        // Materialise so we can iterate twice (summary sheet + detail sheet).
        var locationGroups = perLocation
            .Select(g => (g.LocationName, Entries: g.Entries.ToList()))
            .OrderBy(g => g.LocationName)
            .ToList();

        using var wb = new XLWorkbook();
        var rangeText = (from.HasValue || to.HasValue)
            ? $"Obdobie: {(from?.ToString("dd.MM.yyyy") ?? "začiatok")} – {(to?.ToString("dd.MM.yyyy") ?? "dnes")}"
            : "Obdobie: celé obdobie";

        // ────── Sheet 1: Súhrn pracovísk ──────
        var s1 = wb.Worksheets.Add("Súhrn pracovísk");
        s1.Cell(1, 1).Value = "Spotreba materiálu — všetky pracoviská";
        s1.Range(1, 1, 1, 4).Merge();
        s1.Cell(1, 1).Style.Font.Bold = true;
        s1.Cell(1, 1).Style.Font.FontSize = 14;

        s1.Cell(2, 1).Value = rangeText;
        s1.Range(2, 1, 2, 4).Merge();
        s1.Cell(2, 1).Style.Font.Italic = true;

        // Header
        s1.Cell(4, 1).Value = "Pracovisko";
        s1.Cell(4, 2).Value = "Počet záznamov";
        s1.Cell(4, 3).Value = "Spolu náklady";
        s1.Cell(4, 4).Value = "Posledný záznam";
        var s1Header = s1.Range(4, 1, 4, 4);
        s1Header.Style.Font.Bold = true;
        s1Header.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBBF24"); // amber-400
        s1Header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var row = 5;
        decimal grandTotal = 0m;
        int grandCount = 0;
        foreach (var (name, entries) in locationGroups)
        {
            var totalCost = entries.Sum(e => e.LineCost);
            var lastDate  = entries.Count > 0 ? entries.Max(e => e.Date) : (DateTime?)null;

            s1.Cell(row, 1).Value = name;
            s1.Cell(row, 2).Value = entries.Count;

            var costCell = s1.Cell(row, 3);
            costCell.Value = (double)totalCost;
            costCell.Style.NumberFormat.Format = EurFormat;

            if (lastDate.HasValue)
            {
                s1.Cell(row, 4).Value = lastDate.Value;
                s1.Cell(row, 4).Style.DateFormat.Format = "dd.MM.yyyy";
            }

            grandTotal += totalCost;
            grandCount += entries.Count;
            row++;
        }

        // Grand-total row
        if (locationGroups.Count > 0)
        {
            s1.Cell(row, 1).Value = "Spolu:";
            s1.Cell(row, 1).Style.Font.Bold = true;
            s1.Cell(row, 2).Value = grandCount;
            s1.Cell(row, 2).Style.Font.Bold = true;
            var grand = s1.Cell(row, 3);
            grand.Value = (double)grandTotal;
            grand.Style.NumberFormat.Format = EurFormat;
            grand.Style.Font.Bold = true;
            s1.Range(row, 1, row, 4).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        s1.SheetView.FreezeRows(4);
        s1.Columns().AdjustToContents();

        // ────── Sheet 2: Detailný záznam ──────
        var s2 = wb.Worksheets.Add("Detailný záznam");

        s2.Cell(1, 1).Value = "Pracovisko";
        s2.Cell(1, 2).Value = "Dátum";
        s2.Cell(1, 3).Value = "Materiál";
        s2.Cell(1, 4).Value = "Množstvo";
        s2.Cell(1, 5).Value = "Jednotka";
        s2.Cell(1, 6).Value = "Cena/jednotka";
        s2.Cell(1, 7).Value = "Náklady";
        s2.Cell(1, 8).Value = "Zdroj";
        s2.Cell(1, 9).Value = "Zamestnanec";
        s2.Cell(1,10).Value = "Poznámka";
        var s2Header = s2.Range(1, 1, 1, 10);
        s2Header.Style.Font.Bold = true;
        s2Header.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBBF24");
        s2Header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        row = 2;
        foreach (var (name, entries) in locationGroups)
        {
            foreach (var e in entries.OrderByDescending(x => x.Date))
            {
                s2.Cell(row, 1).Value = name;

                s2.Cell(row, 2).Value = e.Date;
                s2.Cell(row, 2).Style.DateFormat.Format = "dd.MM.yyyy";

                s2.Cell(row, 3).Value = e.MaterialName;
                s2.Cell(row, 4).Value = (double)e.Quantity;
                s2.Cell(row, 5).Value = e.Unit;

                var unitCell = s2.Cell(row, 6);
                unitCell.Value = (double)e.UnitPriceAtTime;
                unitCell.Style.NumberFormat.Format = EurFormat;

                var lineCell = s2.Cell(row, 7);
                lineCell.Value = (double)e.LineCost;
                lineCell.Style.NumberFormat.Format = EurFormat;

                // Source pill matches the in-app badge: invoice-promoted usage,
                // kiosk Nákup pseudo-row, or manual entry.
                string source;
                if (e.FromPurchase) source = "Z nákupu";
                else if (!string.IsNullOrEmpty(e.Note) && e.Note.StartsWith("Faktúra", StringComparison.Ordinal)) source = "Faktúra";
                else source = "Ručne";
                s2.Cell(row, 8).Value = source;

                s2.Cell(row, 9).Value  = e.EmployeeName ?? "";
                s2.Cell(row, 10).Value = e.Note ?? "";
                row++;
            }
        }

        // Detail grand-total row
        if (row > 2)
        {
            s2.Cell(row, 1).Value = "Spolu:";
            s2.Cell(row, 1).Style.Font.Bold = true;
            var detailGrand = s2.Cell(row, 7);
            detailGrand.Value = (double)grandTotal;
            detailGrand.Style.NumberFormat.Format = EurFormat;
            detailGrand.Style.Font.Bold = true;
            s2.Range(row, 1, row, 10).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        s2.SheetView.FreezeRows(1);
        s2.Columns().AdjustToContents();
        s2.Column(2).Width = Math.Max(s2.Column(2).Width, 14);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
