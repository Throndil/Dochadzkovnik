using ClosedXML.Excel;
using API.DTOs;

namespace API.Services;

public interface IPayrollExcelExportService
{
    /// <summary>
    /// Monthly Mzdy summary — one row per employee, mirrors the on-screen
    /// table. Slovak headers; EUR cells formatted as <c>#,##0.00 €</c>.
    /// </summary>
    byte[] BuildMonthlySummary(string month, PayrollMonthlyDto data);

    /// <summary>
    /// Per-employee comprehensive workbook — the "monthly paycheck" with
    /// everywhere the worker was, photos they took, cars they drove, and
    /// advances. Sheets: Súhrn, Dni, Pracoviská, Vozidlá, Fotky, Zálohy.
    /// </summary>
    byte[] BuildEmployeeReport(EmployeeReportData data);

    /// <summary>
    /// Per-location Náklady a zisk (P&amp;L) workbook — mirrors the card on
    /// the Location detail page: Príjem (zmluvná hodnota), Mzdové náklady per
    /// employee, Materiálové náklady per material (when the MaterialPurchases
    /// flag is on), Čistý zisk + marža.
    /// </summary>
    byte[] BuildLocationPnlReport(LocationPnlDto data, DateTime? from, DateTime? to);
}

/// <summary>
/// Plain-data carrier so the controller can pull everything it needs in one
/// shot and pass it to the workbook builder without the service having to
/// touch <c>AppDbContext</c>.
/// </summary>
public sealed record EmployeeReportData(
    int EmployeeId,
    string FullName,
    string Month,                 // "yyyy-MM"
    DateTime PeriodFrom,
    DateTime PeriodTo,             // exclusive
    decimal? CurrentHourlyWage,
    IReadOnlyList<EmployeeDayRow> Days,
    IReadOnlyList<EmployeeLocationRow> Locations,
    IReadOnlyList<EmployeeCarRow> Cars,
    IReadOnlyList<EmployeePhotoRow> Photos,
    IReadOnlyList<EmployeeAdvanceDto> Advances);

public sealed record EmployeeDayRow(
    DateTime Date,
    string LocationName,
    string? CarName,
    decimal Hours,
    decimal WageAtTime,
    decimal Earnings,
    bool HasDiary,
    string? PhotoUrl,
    string? Note);

public sealed record EmployeeLocationRow(
    string LocationName,
    decimal Hours,
    decimal Earnings,
    int ShiftCount);

public sealed record EmployeeCarRow(
    string CarName,
    string? LicensePlate,
    decimal Hours,
    int ShiftCount);

public sealed record EmployeePhotoRow(
    DateTime CapturedAt,
    string LocationName,
    string PhotoUrl,
    string Source,        // "Šichta" or "Foto pracoviska"
    string? Note)
{
    /// <summary>
    /// Downloaded thumbnail bytes (JPEG) for the embedded preview, or null when
    /// the fetch failed / was skipped. Populated by the controller before the
    /// workbook is built; the Fotky sheet embeds it next to the link.
    /// </summary>
    public byte[]? ThumbnailBytes { get; set; }
}

public class PayrollExcelExportService : IPayrollExcelExportService
{
    private const string EurFormat = "#,##0.00 €";
    private const string HoursFormat = "#,##0.00\" h\"";
    private const string DateFormat = "dd.MM.yyyy";

    public byte[] BuildMonthlySummary(string month, PayrollMonthlyDto data)
    {
        using var wb = new XLWorkbook();
        var s = wb.Worksheets.Add("Mzdy");

        s.Cell(1, 1).Value = $"Mzdy — {month}";
        s.Range(1, 1, 1, 6).Merge();
        s.Cell(1, 1).Style.Font.Bold = true;
        s.Cell(1, 1).Style.Font.FontSize = 14;

        var headerRow = 3;
        s.Cell(headerRow, 1).Value = "Meno";
        s.Cell(headerRow, 2).Value = "Hodiny";
        s.Cell(headerRow, 3).Value = "Hodinová sadzba";
        s.Cell(headerRow, 4).Value = "Hrubá mzda";
        s.Cell(headerRow, 5).Value = "Zálohy";
        s.Cell(headerRow, 6).Value = "Výplata";
        var headerRange = s.Range(headerRow, 1, headerRow, 6);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var row = headerRow + 1;
        foreach (var r in data.Rows)
        {
            s.Cell(row, 1).Value = r.FirstName + " " + r.LastName + (r.IsActive ? "" : " (neaktívny)");
            s.Cell(row, 2).Value = (double)r.HoursWorked;
            s.Cell(row, 2).Style.NumberFormat.Format = HoursFormat;
            if (r.HourlyWageSnapshotAvg.HasValue)
            {
                s.Cell(row, 3).Value = (double)r.HourlyWageSnapshotAvg.Value;
                s.Cell(row, 3).Style.NumberFormat.Format = EurFormat;
            }
            else
            {
                s.Cell(row, 3).Value = "Sadzba nenastavená";
                s.Cell(row, 3).Style.Font.FontColor = XLColor.DarkOrange;
            }
            s.Cell(row, 4).Value = (double)r.Gross;
            s.Cell(row, 4).Style.NumberFormat.Format = EurFormat;
            s.Cell(row, 5).Value = (double)r.AdvancesTotal;
            s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
            s.Cell(row, 6).Value = (double)r.Payout;
            s.Cell(row, 6).Style.NumberFormat.Format = EurFormat;
            s.Cell(row, 6).Style.Font.Bold = true;
            if (r.Payout < 0m)
                s.Cell(row, 6).Style.Font.FontColor = XLColor.Red; // zálohy prevyšujú mzdu
            row++;
        }

        // Totals footer
        s.Cell(row, 1).Value = "Spolu";
        s.Cell(row, 1).Style.Font.Bold = true;
        s.Cell(row, 2).Value = (double)data.Totals.HoursWorked;
        s.Cell(row, 2).Style.NumberFormat.Format = HoursFormat;
        s.Cell(row, 4).Value = (double)data.Totals.Gross;
        s.Cell(row, 4).Style.NumberFormat.Format = EurFormat;
        s.Cell(row, 5).Value = (double)data.Totals.AdvancesTotal;
        s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
        s.Cell(row, 6).Value = (double)data.Totals.Payout;
        s.Cell(row, 6).Style.NumberFormat.Format = EurFormat;
        s.Range(row, 1, row, 6).Style.Font.Bold = true;
        s.Range(row, 1, row, 6).Style.Border.TopBorder = XLBorderStyleValues.Thin;

        s.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public byte[] BuildEmployeeReport(EmployeeReportData d)
    {
        using var wb = new XLWorkbook();

        // ─── Sheet 1: Súhrn ────────────────────────────────────────────
        var s1 = wb.Worksheets.Add("Súhrn");

        s1.Cell(1, 1).Value = $"Mzdový list — {d.FullName}";
        s1.Range(1, 1, 1, 4).Merge();
        s1.Cell(1, 1).Style.Font.Bold = true;
        s1.Cell(1, 1).Style.Font.FontSize = 16;

        s1.Cell(2, 1).Value = $"Obdobie: {d.PeriodFrom:dd.MM.yyyy} – {d.PeriodTo.AddDays(-1):dd.MM.yyyy}";
        s1.Range(2, 1, 2, 4).Merge();
        s1.Cell(2, 1).Style.Font.Italic = true;

        var totalHours    = d.Days.Sum(x => x.Hours);
        var totalEarnings = d.Days.Sum(x => x.Earnings);
        var totalAdv      = d.Advances.Sum(x => x.Amount);
        var avgRate       = totalHours > 0 ? totalEarnings / totalHours : 0m;

        var row = 4;
        WriteKV(s1, row++, "Odpracované hodiny", totalHours, HoursFormat);
        WriteKV(s1, row++, "Priemerná sadzba (vážená)", avgRate, EurFormat);
        if (d.CurrentHourlyWage.HasValue)
            WriteKV(s1, row++, "Aktuálna sadzba (katalóg)", d.CurrentHourlyWage.Value, EurFormat);
        else
        {
            s1.Cell(row, 1).Value = "Aktuálna sadzba (katalóg)";
            s1.Cell(row, 2).Value = "Sadzba nenastavená";
            s1.Cell(row, 2).Style.Font.FontColor = XLColor.DarkOrange;
            row++;
        }
        WriteKV(s1, row++, "Hrubá mzda", totalEarnings, EurFormat);
        WriteKV(s1, row++, "Zálohy spolu", totalAdv, EurFormat);
        WriteKV(s1, row++, "VÝPLATA", totalEarnings - totalAdv, EurFormat);
        s1.Cell(row - 1, 1).Style.Font.Bold = true;
        s1.Cell(row - 1, 2).Style.Font.Bold = true;
        s1.Cell(row - 1, 2).Style.Font.FontSize = 13;

        row += 2;
        WriteKV(s1, row++, "Počet odpracovaných šícht", d.Days.Count, "#,##0");
        WriteKV(s1, row++, "Pracoviská (počet)", d.Locations.Count, "#,##0");
        WriteKV(s1, row++, "Použité vozidlá (počet)", d.Cars.Count, "#,##0");
        WriteKV(s1, row++, "Fotky (počet)", d.Photos.Count, "#,##0");

        s1.Columns().AdjustToContents();

        // ─── Sheet 2: Dni ──────────────────────────────────────────────
        var s2 = wb.Worksheets.Add("Dni");
        WriteHeaders(s2, "Dátum", "Pracovisko", "Vozidlo", "Hodiny", "Sadzba", "Zárobok", "Denník", "Fotka", "Poznámka");
        var r = 2;
        foreach (var day in d.Days.OrderBy(x => x.Date))
        {
            s2.Cell(r, 1).Value = day.Date;
            s2.Cell(r, 1).Style.NumberFormat.Format = DateFormat;
            s2.Cell(r, 2).Value = day.LocationName;
            s2.Cell(r, 3).Value = day.CarName ?? "—";
            s2.Cell(r, 4).Value = (double)day.Hours;
            s2.Cell(r, 4).Style.NumberFormat.Format = HoursFormat;
            s2.Cell(r, 5).Value = (double)day.WageAtTime;
            s2.Cell(r, 5).Style.NumberFormat.Format = EurFormat;
            s2.Cell(r, 6).Value = (double)day.Earnings;
            s2.Cell(r, 6).Style.NumberFormat.Format = EurFormat;
            s2.Cell(r, 7).Value = day.HasDiary ? "Áno" : "—";
            if (!string.IsNullOrEmpty(day.PhotoUrl))
            {
                s2.Cell(r, 8).Value = "Otvoriť";
                s2.Cell(r, 8).SetHyperlink(new XLHyperlink(day.PhotoUrl));
                s2.Cell(r, 8).Style.Font.FontColor = XLColor.Blue;
                s2.Cell(r, 8).Style.Font.Underline = XLFontUnderlineValues.Single;
            }
            else s2.Cell(r, 8).Value = "—";
            s2.Cell(r, 9).Value = day.Note ?? "";
            r++;
        }
        s2.Columns().AdjustToContents();

        // ─── Sheet 3: Pracoviská ───────────────────────────────────────
        var s3 = wb.Worksheets.Add("Pracoviská");
        WriteHeaders(s3, "Pracovisko", "Šichty", "Hodiny", "Zárobok");
        r = 2;
        foreach (var loc in d.Locations.OrderByDescending(x => x.Hours))
        {
            s3.Cell(r, 1).Value = loc.LocationName;
            s3.Cell(r, 2).Value = loc.ShiftCount;
            s3.Cell(r, 3).Value = (double)loc.Hours;
            s3.Cell(r, 3).Style.NumberFormat.Format = HoursFormat;
            s3.Cell(r, 4).Value = (double)loc.Earnings;
            s3.Cell(r, 4).Style.NumberFormat.Format = EurFormat;
            r++;
        }
        s3.Columns().AdjustToContents();

        // ─── Sheet 4: Vozidlá ──────────────────────────────────────────
        var s4 = wb.Worksheets.Add("Vozidlá");
        WriteHeaders(s4, "Vozidlo", "EČV", "Šichty", "Hodiny");
        r = 2;
        foreach (var car in d.Cars.OrderByDescending(x => x.Hours))
        {
            s4.Cell(r, 1).Value = car.CarName;
            s4.Cell(r, 2).Value = car.LicensePlate ?? "—";
            s4.Cell(r, 3).Value = car.ShiftCount;
            s4.Cell(r, 4).Value = (double)car.Hours;
            s4.Cell(r, 4).Style.NumberFormat.Format = HoursFormat;
            r++;
        }
        s4.Columns().AdjustToContents();

        // ─── Sheet 5: Fotky ────────────────────────────────────────────
        // Each row carries the metadata + a clickable link, plus an embedded
        // thumbnail (when the controller managed to download one). The image is
        // anchored into the "Náhľad" column; the row is given enough height to
        // show it.
        var s5 = wb.Worksheets.Add("Fotky");
        WriteHeaders(s5, "Dátum", "Pracovisko", "Zdroj", "Poznámka", "Odkaz", "Náhľad");
        r = 2;
        foreach (var p in d.Photos.OrderBy(x => x.CapturedAt))
        {
            s5.Cell(r, 1).Value = p.CapturedAt;
            s5.Cell(r, 1).Style.NumberFormat.Format = "dd.MM.yyyy HH:mm";
            s5.Cell(r, 2).Value = p.LocationName;
            s5.Cell(r, 3).Value = p.Source;
            s5.Cell(r, 4).Value = p.Note ?? "";
            s5.Cell(r, 5).Value = "Otvoriť fotku";
            s5.Cell(r, 5).SetHyperlink(new XLHyperlink(p.PhotoUrl));
            s5.Cell(r, 5).Style.Font.FontColor = XLColor.Blue;
            s5.Cell(r, 5).Style.Font.Underline = XLFontUnderlineValues.Single;

            if (p.ThumbnailBytes is { Length: > 0 } bytes)
            {
                try
                {
                    using var imgStream = new MemoryStream(bytes);
                    // Format is auto-detected from the stream bytes (the enum
                    // XLPictureFormat lives in ClosedXML.Excel.Drawings; no need
                    // to depend on it here).
                    s5.AddPicture(imgStream)
                        .MoveTo(s5.Cell(r, 6), 3, 3)
                        .WithSize(160, 120);
                    s5.Row(r).Height = 92; // ~123 px, fits the 120 px image
                }
                catch
                {
                    // A malformed/unsupported image must never break the export —
                    // the link in column 5 is still there.
                }
            }
            r++;
        }
        // Adjust text columns only; the picture column gets a fixed width so the
        // embedded thumbnails aren't squeezed.
        for (var c = 1; c <= 5; c++) s5.Column(c).AdjustToContents();
        s5.Column(6).Width = 24;

        // ─── Sheet 6: Zálohy ───────────────────────────────────────────
        var s6 = wb.Worksheets.Add("Zálohy");
        WriteHeaders(s6, "Dátum", "Suma", "Poznámka", "Zaznamenal");
        r = 2;
        foreach (var a in d.Advances.OrderBy(x => x.Date))
        {
            s6.Cell(r, 1).Value = a.Date;
            s6.Cell(r, 1).Style.NumberFormat.Format = DateFormat;
            s6.Cell(r, 2).Value = (double)a.Amount;
            s6.Cell(r, 2).Style.NumberFormat.Format = EurFormat;
            s6.Cell(r, 3).Value = a.Note ?? "";
            s6.Cell(r, 4).Value = a.CreatedBy ?? "";
            r++;
        }
        if (d.Advances.Count > 0)
        {
            s6.Cell(r, 1).Value = "Spolu";
            s6.Cell(r, 1).Style.Font.Bold = true;
            s6.Cell(r, 2).Value = (double)d.Advances.Sum(x => x.Amount);
            s6.Cell(r, 2).Style.NumberFormat.Format = EurFormat;
            s6.Cell(r, 2).Style.Font.Bold = true;
            s6.Range(r, 1, r, 4).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }
        s6.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public byte[] BuildLocationPnlReport(LocationPnlDto d, DateTime? from, DateTime? to)
    {
        using var wb = new XLWorkbook();
        var s = wb.Worksheets.Add("Náklady a zisk");

        s.Cell(1, 1).Value = $"Náklady a zisk — {d.Location.Name}";
        s.Range(1, 1, 1, 5).Merge();
        s.Cell(1, 1).Style.Font.Bold = true;
        s.Cell(1, 1).Style.Font.FontSize = 14;

        var rangeText = (from.HasValue || to.HasValue)
            ? $"Obdobie: {(from?.ToString("dd.MM.yyyy") ?? "začiatok")} – {(to?.ToString("dd.MM.yyyy") ?? "dnes")}"
            : "Obdobie: celé obdobie";
        s.Cell(2, 1).Value = rangeText;
        s.Range(2, 1, 2, 5).Merge();
        s.Cell(2, 1).Style.Font.Italic = true;

        var row = 4;

        // ── Príjem ─────────────────────────────────────────────────────
        s.Cell(row, 1).Value = "Príjem (zmluvná hodnota)";
        s.Cell(row, 1).Style.Font.Bold = true;
        if (d.Revenue.HasValue)
        {
            s.Cell(row, 5).Value = (double)d.Revenue.Value;
            s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
        }
        else
        {
            s.Cell(row, 5).Value = "—";
        }
        s.Cell(row, 5).Style.Font.Bold = true;
        row += 2;

        // ── Mzdové náklady (per-employee breakdown) ───────────────────
        s.Cell(row, 1).Value = "Mzdové náklady";
        s.Cell(row, 1).Style.Font.Bold = true;
        s.Cell(row, 5).Value = (double)d.Labour.Cost;
        s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
        s.Cell(row, 5).Style.Font.Bold = true;
        s.Range(row, 1, row, 5).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        row++;

        if (d.Labour.BreakdownByEmployee.Count > 0)
        {
            s.Cell(row, 1).Value = "Zamestnanec";
            s.Cell(row, 2).Value = "Hodiny";
            s.Cell(row, 4).Value = "Priem. sadzba";
            s.Cell(row, 5).Value = "Náklady";
            var lh = s.Range(row, 1, row, 5);
            lh.Style.Font.Bold = true;
            lh.Style.Fill.BackgroundColor = XLColor.LightGray;
            row++;

            foreach (var e in d.Labour.BreakdownByEmployee)
            {
                s.Cell(row, 1).Value = e.EmployeeName;
                s.Cell(row, 2).Value = (double)e.Hours;
                s.Cell(row, 2).Style.NumberFormat.Format = HoursFormat;
                if (e.AvgWage.HasValue)
                {
                    s.Cell(row, 4).Value = (double)e.AvgWage.Value;
                    s.Cell(row, 4).Style.NumberFormat.Format = EurFormat;
                }
                s.Cell(row, 5).Value = (double)e.Cost;
                s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
                row++;
            }
        }
        row++;

        // ── Materiálové náklady (null = MaterialPurchases flag off) ────
        if (d.Material != null)
        {
            s.Cell(row, 1).Value = "Materiálové náklady";
            s.Cell(row, 1).Style.Font.Bold = true;
            s.Cell(row, 5).Value = (double)d.Material.Cost;
            s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
            s.Cell(row, 5).Style.Font.Bold = true;
            s.Range(row, 1, row, 5).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            row++;

            if (d.Material.BreakdownByMaterial.Count > 0)
            {
                s.Cell(row, 1).Value = "Materiál";
                s.Cell(row, 2).Value = "Množstvo";
                s.Cell(row, 3).Value = "Jednotka";
                s.Cell(row, 4).Value = "Priem. cena";
                s.Cell(row, 5).Value = "Náklady";
                var mh = s.Range(row, 1, row, 5);
                mh.Style.Font.Bold = true;
                mh.Style.Fill.BackgroundColor = XLColor.LightGray;
                row++;

                foreach (var m in d.Material.BreakdownByMaterial)
                {
                    s.Cell(row, 1).Value = m.MaterialName;
                    s.Cell(row, 2).Value = (double)m.Quantity;
                    s.Cell(row, 2).Style.NumberFormat.Format = "#,##0.###";
                    s.Cell(row, 3).Value = m.Unit;
                    if (m.AvgUnitPrice.HasValue)
                    {
                        s.Cell(row, 4).Value = (double)m.AvgUnitPrice.Value;
                        s.Cell(row, 4).Style.NumberFormat.Format = EurFormat;
                    }
                    s.Cell(row, 5).Value = (double)m.Cost;
                    s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
                    row++;
                }
            }
            row++;
        }

        // ── Výjazdy áut (F5) ───────────────────────────────────────────
        if (d.Trips != null)
        {
            s.Cell(row, 1).Value = "Výjazdy áut";
            s.Cell(row, 1).Style.Font.Bold = true;
            s.Cell(row, 2).Value = $"{d.Trips.Count} × {d.Trips.Rate:0.00} €";
            s.Cell(row, 5).Value = (double)d.Trips.Cost;
            s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
            s.Cell(row, 5).Style.Font.Bold = true;
            s.Range(row, 1, row, 5).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            row += 2;
        }

        // ── Čistý zisk + marža (hidden when no contract value) ─────────
        if (d.Profit.HasValue)
        {
            s.Cell(row, 1).Value = "Čistý zisk";
            s.Cell(row, 1).Style.Font.Bold = true;
            s.Cell(row, 5).Value = (double)d.Profit.Value;
            s.Cell(row, 5).Style.NumberFormat.Format = EurFormat;
            s.Cell(row, 5).Style.Font.Bold = true;
            s.Cell(row, 5).Style.Font.FontSize = 12;
            s.Range(row, 1, row, 5).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            row++;

            if (d.Revenue is decimal rev && rev != 0m)
            {
                s.Cell(row, 1).Value = "Marža";
                s.Cell(row, 5).Value = (double)(d.Profit.Value / rev);
                s.Cell(row, 5).Style.NumberFormat.Format = "0 %";
            }
        }

        s.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteKV(IXLWorksheet s, int row, string label, decimal value, string fmt)
    {
        s.Cell(row, 1).Value = label;
        s.Cell(row, 2).Value = (double)value;
        s.Cell(row, 2).Style.NumberFormat.Format = fmt;
    }

    private static void WriteKV(IXLWorksheet s, int row, string label, int value, string fmt)
    {
        s.Cell(row, 1).Value = label;
        s.Cell(row, 2).Value = value;
        s.Cell(row, 2).Style.NumberFormat.Format = fmt;
    }

    private static void WriteHeaders(IXLWorksheet s, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            s.Cell(1, i + 1).Value = headers[i];
            s.Cell(1, i + 1).Style.Font.Bold = true;
            s.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }
    }
}
