using ClosedXML.Excel;

namespace API.Services;

/// <summary>One document row for the D6 monthly division report.</summary>
public sealed record DivisionReportDoc(
    DateTime IssueDate,
    string InvoiceNumber,
    string SupplierName,
    string DocumentKind,   // "invoice" | "receipt"
    string Direction,      // "cost" | "income"
    string Division,       // "profistav" | "stroje" ('' legacy counts as profistav)
    decimal TotalInclVat,
    string Status);        // "review" | "committed" | "parsing"

/// <summary>
/// D6 — mesačný report divízií: one workbook with a Súhrn sheet (príjem /
/// výdaj / rozdiel per division + spolu) and a per-division document
/// listing. Amounts are s DPH, from the printed document totals — the same
/// numbers the Divízie card on the Súhrn shows.
/// </summary>
public static class DivisionMonthlyReportBuilder
{
    private const string EurFormat = "#,##0.00 €";

    public static byte[] Build(string monthLabel, IReadOnlyList<DivisionReportDoc> docs)
    {
        using var wb = new XLWorkbook();

        var profistav = docs.Where(d => d.Division != "stroje").ToList();
        var stroje    = docs.Where(d => d.Division == "stroje").ToList();

        BuildSummarySheet(wb, monthLabel, profistav, stroje);
        BuildDivisionSheet(wb, "AZ Profistav", monthLabel, profistav);
        BuildDivisionSheet(wb, "AZ Stroje", monthLabel, stroje);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static decimal Income(IEnumerable<DivisionReportDoc> docs)
        => docs.Where(d => d.Direction == "income").Sum(d => d.TotalInclVat);
    private static decimal Expense(IEnumerable<DivisionReportDoc> docs)
        => docs.Where(d => d.Direction != "income").Sum(d => d.TotalInclVat);

    private static void BuildSummarySheet(XLWorkbook wb, string monthLabel,
        List<DivisionReportDoc> profistav, List<DivisionReportDoc> stroje)
    {
        var s = wb.Worksheets.Add("Súhrn");

        s.Cell(1, 1).Value = $"Report divízií — {monthLabel}";
        s.Range(1, 1, 1, 5).Merge();
        s.Cell(1, 1).Style.Font.Bold = true;
        s.Cell(1, 1).Style.Font.FontSize = 14;

        var header = 3;
        s.Cell(header, 1).Value = "Divízia";
        s.Cell(header, 2).Value = "Príjem";
        s.Cell(header, 3).Value = "Výdaj";
        s.Cell(header, 4).Value = "Rozdiel";
        s.Cell(header, 5).Value = "Doklady";
        var hr = s.Range(header, 1, header, 5);
        hr.Style.Font.Bold = true;
        hr.Style.Fill.BackgroundColor = XLColor.LightGray;
        hr.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var row = header + 1;
        foreach (var (label, docs) in new[] { ("AZ Profistav", profistav), ("AZ Stroje", stroje) })
        {
            s.Cell(row, 1).Value = label;
            WriteMoney(s, row, 2, Income(docs));
            WriteMoney(s, row, 3, Expense(docs));
            WriteMoney(s, row, 4, Income(docs) - Expense(docs));
            s.Cell(row, 5).Value = docs.Count;
            row++;
        }

        var all = profistav.Concat(stroje).ToList();
        s.Cell(row, 1).Value = "Spolu";
        WriteMoney(s, row, 2, Income(all));
        WriteMoney(s, row, 3, Expense(all));
        WriteMoney(s, row, 4, Income(all) - Expense(all));
        s.Cell(row, 5).Value = all.Count;
        var tr = s.Range(row, 1, row, 5);
        tr.Style.Font.Bold = true;
        tr.Style.Border.TopBorder = XLBorderStyleValues.Thin;

        s.Columns().AdjustToContents();
    }

    private static void BuildDivisionSheet(XLWorkbook wb, string name, string monthLabel, List<DivisionReportDoc> docs)
    {
        var s = wb.Worksheets.Add(name);

        s.Cell(1, 1).Value = $"{name} — {monthLabel}";
        s.Range(1, 1, 1, 7).Merge();
        s.Cell(1, 1).Style.Font.Bold = true;
        s.Cell(1, 1).Style.Font.FontSize = 14;

        var header = 3;
        var titles = new[] { "Dátum", "Číslo dokladu", "Dodávateľ", "Typ", "Smer", "Suma s DPH", "Stav" };
        for (var i = 0; i < titles.Length; i++) s.Cell(header, i + 1).Value = titles[i];
        var hr = s.Range(header, 1, header, 7);
        hr.Style.Font.Bold = true;
        hr.Style.Fill.BackgroundColor = XLColor.LightGray;
        hr.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var row = header + 1;
        foreach (var d in docs.OrderBy(x => x.IssueDate))
        {
            s.Cell(row, 1).Value = d.IssueDate;
            s.Cell(row, 1).Style.NumberFormat.Format = "dd.MM.yyyy";
            s.Cell(row, 2).Value = d.InvoiceNumber;
            s.Cell(row, 3).Value = d.SupplierName;
            s.Cell(row, 4).Value = d.DocumentKind == "receipt" ? "Bloček" : "Faktúra";
            s.Cell(row, 5).Value = d.Direction == "income" ? "Príjem" : "Výdaj";
            // Signed: income +, cost − so the column sums to the Rozdiel.
            WriteMoney(s, row, 6, d.Direction == "income" ? d.TotalInclVat : -d.TotalInclVat);
            s.Cell(row, 7).Value = d.Status == "committed" ? "Uložený" : "Na kontrole";
            row++;
        }

        if (docs.Count == 0)
        {
            s.Cell(row, 1).Value = "Žiadne doklady v tomto mesiaci.";
            s.Cell(row, 1).Style.Font.Italic = true;
        }
        else
        {
            s.Cell(row, 5).Value = "Príjem";
            WriteMoney(s, row, 6, Income(docs));
            s.Cell(row + 1, 5).Value = "Výdaj";
            WriteMoney(s, row + 1, 6, Expense(docs));
            s.Cell(row + 2, 5).Value = "Rozdiel";
            WriteMoney(s, row + 2, 6, Income(docs) - Expense(docs));
            var tr = s.Range(row, 5, row + 2, 6);
            tr.Style.Font.Bold = true;
            s.Range(row, 1, row, 7).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        s.Columns().AdjustToContents();
    }

    private static void WriteMoney(IXLWorksheet s, int row, int col, decimal value)
    {
        s.Cell(row, col).Value = (double)value;
        s.Cell(row, col).Style.NumberFormat.Format = EurFormat;
    }
}
