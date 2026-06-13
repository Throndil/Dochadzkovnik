using System.Globalization;
using System.Text;
using API.Data;
using API.DTOs;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    public ReportsController(AppDbContext db, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
    }

    /// Insert Cloudinary transform (c_fill + forced JPEG) after /upload/
    private static string CloudinaryThumb(string url, int w, int h) =>
        url.Contains("/upload/")
            ? url.Replace("/upload/", $"/upload/c_fill,h_{h},w_{w},f_jpg/")
            : url;

    private static void StyleHeader(IXLCell cell, string text)
    {
        cell.Value = text;
        cell.Style.Font.Bold = true;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#f59e0b");
        cell.Style.Font.FontColor = XLColor.White;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    [HttpGet("daily")]
    public async Task<ActionResult<DailyReportDto>> GetDaily([FromQuery] DateTime? date)
    {
        var d = (date ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz)).Date;

        var entries = await _db.TimeEntries
            .Include(t => t.Employee)
            .Include(t => t.Location)
            .Include(t => t.Car)
            .Where(t => t.ClockIn.Date == d)
            .OrderBy(t => t.ClockIn)
            .ToListAsync();

        return new DailyReportDto
        {
            Date = d,
            Entries = entries.Select(t => new DailyReportEntryDto
            {
                EmployeeName = $"{t.Employee.FirstName} {t.Employee.LastName}",
                LocationName = t.Location.Name,
                CarName = t.Car?.Name,
                ClockIn = t.ClockIn,
                ClockOut = t.ClockOut,
                HoursWorked = t.ClockOut.HasValue ? (t.ClockOut.Value - t.ClockIn).TotalHours : null
            }).ToList(),
            TotalHours = entries
                .Where(t => t.ClockOut.HasValue)
                .Sum(t => (t.ClockOut!.Value - t.ClockIn).TotalHours)
        };
    }

    [HttpGet("summary")]
    public async Task<ActionResult<List<TimeEntryDto>>> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? employeeId,
        [FromQuery] int? locationId)
    {
        var query = _db.TimeEntries
            .Include(t => t.Employee)
            .Include(t => t.Location)
            .Include(t => t.Car)
            .AsQueryable();

        if (from.HasValue) query = query.Where(t => t.ClockIn >= from.Value);
        if (to.HasValue) query = query.Where(t => t.ClockIn < to.Value.AddDays(1));
        if (employeeId.HasValue) query = query.Where(t => t.EmployeeId == employeeId);
        if (locationId.HasValue) query = query.Where(t => t.LocationId == locationId);

        return await query
            .OrderByDescending(t => t.ClockIn)
            .Select(t => new TimeEntryDto
            {
                Id = t.Id,
                EmployeeId = t.EmployeeId,
                EmployeeName = t.Employee.FirstName + " " + t.Employee.LastName,
                EmployeePhotoUrl = t.Employee.PhotoUrl,
                LocationId = t.LocationId,
                LocationName = t.Location.Name,
                CarId = t.CarId,
                CarName = t.Car != null ? t.Car.Name : null,
                ClockIn = t.ClockIn,
                ClockOut = t.ClockOut,
                HoursWorked = t.ClockOut.HasValue
                    ? (t.ClockOut.Value - t.ClockIn).TotalHours
                    : null,
                Note = t.Note,
                ProofOfWorkSkipped = t.ProofOfWorkSkipped,
                HasDiary = _db.WorkDiaries.Any(d => d.TimeEntryId == t.Id),
                DiaryBody = _db.WorkDiaries
                               .Where(d => d.TimeEntryId == t.Id)
                               .OrderBy(d => d.Id)
                               .Select(d => d.BodyText)
                               .FirstOrDefault()
            })
            .ToListAsync();
    }

    [HttpGet("export/csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? employeeId,
        [FromQuery] int? locationId)
    {
        var query = _db.TimeEntries
            .Include(t => t.Employee)
            .Include(t => t.Location)
            .Include(t => t.Car)
            .AsQueryable();

        if (from.HasValue) query = query.Where(t => t.ClockIn >= from.Value);
        if (to.HasValue) query = query.Where(t => t.ClockIn < to.Value.AddDays(1));
        if (employeeId.HasValue) query = query.Where(t => t.EmployeeId == employeeId);
        if (locationId.HasValue) query = query.Where(t => t.LocationId == locationId);

        var entries = await query.OrderBy(t => t.ClockIn).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Dátum,Zamestnanec,Pracovisko,Auto,Hodiny,Poznámka,Foto");

        foreach (var t in entries)
        {
            var date = t.ClockIn.ToString("dd.MM.yyyy");

            var hours = "";
            if (t.ClockOut.HasValue)
            {
                var ts = t.ClockOut.Value - t.ClockIn;
                hours = $"{(int)ts.TotalHours}:{ts.Minutes:D2}";
            }
            var name = $"{t.Employee.FirstName} {t.Employee.LastName}";
            var note = t.Note?.Replace("\"", "\"\"") ?? "";
            var car = t.Car?.Name ?? "";
            var photo = t.PhotoUrl ?? "";

            sb.AppendLine($"\"{date}\",\"{name}\",\"{t.Location.Name}\",\"{car}\",{hours},\"{note}\",\"{photo}\"");
        }

        // UTF-8 BOM so Excel opens Slovak characters (č, š, ž …) correctly without import wizard.
        // GetBytes() never includes the preamble — must prepend it explicitly.
        var enc = System.Text.Encoding.UTF8;
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var content = enc.GetBytes(sb.ToString());
        var csvBytes = new byte[bom.Length + content.Length];
        bom.CopyTo(csvBytes, 0);
        content.CopyTo(csvBytes, bom.Length);
        return File(csvBytes, "text/csv", "zaznamy-dochadzky.csv");
    }

    [HttpGet("export/xlsx")]
    public async Task<IActionResult> ExportXlsx(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? employeeId,
        [FromQuery] int? locationId)
    {
        var query = _db.TimeEntries
            .Include(t => t.Employee)
            .Include(t => t.Location)
            .Include(t => t.Car)
            .AsQueryable();

        if (from.HasValue)       query = query.Where(t => t.ClockIn >= from.Value);
        if (to.HasValue)         query = query.Where(t => t.ClockIn < to.Value.AddDays(1));
        if (employeeId.HasValue) query = query.Where(t => t.EmployeeId == employeeId);
        if (locationId.HasValue) query = query.Where(t => t.LocationId == locationId);

        var entries = await query.OrderBy(t => t.ClockIn).ToListAsync();
        var http = _httpClientFactory.CreateClient();

        const int MAX_PHOTOS   = 5;
        const int THUMB_COL    = 7;  // columns G … K  → thumbnails
        const int LINK_COL     = 7 + MAX_PHOTOS; // columns L … P → hyperlinks

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Dochádzka");

        // ── Header row ────────────────────────────────────────────────────
        string[] baseHeaders = ["Dátum", "Zamestnanec", "Pracovisko", "Auto", "Hodiny", "Poznámka"];
        for (int c = 0; c < baseHeaders.Length; c++)
        {
            StyleHeader(ws.Cell(1, c + 1), baseHeaders[c]);
        }
        for (int pi = 0; pi < MAX_PHOTOS; pi++)
        {
            StyleHeader(ws.Cell(1, THUMB_COL + pi), $"Foto {pi + 1}");
            StyleHeader(ws.Cell(1, LINK_COL  + pi), $"Link {pi + 1}");
        }
        ws.SheetView.FreezeRows(1);

        // ── Data rows ─────────────────────────────────────────────────────
        int row = 2;
        foreach (var t in entries)
        {
            var dateStr  = t.ClockIn.ToString("dd.MM.yyyy");
            var hoursStr = t.ClockOut.HasValue
                ? $"{(int)(t.ClockOut.Value - t.ClockIn).TotalHours}:{(t.ClockOut.Value - t.ClockIn).Minutes:D2}"
                : "";
            var fullName = $"{t.Employee.FirstName} {t.Employee.LastName}";

            ws.Cell(row, 1).Value = dateStr;
            ws.Cell(row, 2).Value = fullName;
            ws.Cell(row, 3).Value = t.Location.Name;
            ws.Cell(row, 4).Value = t.Car?.Name ?? "";
            ws.Cell(row, 5).Value = hoursStr;
            ws.Cell(row, 6).Value = t.Note ?? "";
            ws.Row(row).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            // ── One thumbnail + one hyperlink per photo ────────────────────
            var photoUrls = (t.PhotoUrl ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MAX_PHOTOS)
                .ToList();

            if (photoUrls.Count > 0)
            {
                ws.Row(row).Height = 52; // ≈ 69 px

                for (int pi = 0; pi < photoUrls.Count; pi++)
                {
                    var thumbUrl = CloudinaryThumb(photoUrls[pi], 55, 50);
                    try
                    {
                        var imgBytes = await http.GetByteArrayAsync(thumbUrl);
                        using var imgStream = new MemoryStream(imgBytes);
                        ws.AddPicture(imgStream)
                            .MoveTo(ws.Cell(row, THUMB_COL + pi), new System.Drawing.Point(2, 2))
                            .WithSize(55, 48);
                    }
                    catch
                    {
                        ws.Cell(row, THUMB_COL + pi).Value = $"Foto {pi + 1}";
                    }

                    // Clickable hyperlink in the link column
                    var linkCell = ws.Cell(row, LINK_COL + pi);
                    linkCell.Value = $"Foto {pi + 1}";
                    linkCell.SetHyperlink(new XLHyperlink(photoUrls[pi]));
                    linkCell.Style.Font.FontColor       = XLColor.FromHtml("#2563eb");
                    linkCell.Style.Font.Underline       = XLFontUnderlineValues.Single;
                    linkCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }
            }

            row++;
        }

        // ── Column widths ─────────────────────────────────────────────────
        ws.Column(1).Width = 14;
        ws.Column(2).Width = 22;
        ws.Column(3).Width = 22;
        ws.Column(4).Width = 14;
        ws.Column(5).Width = 10;
        ws.Column(6).Width = 30;
        for (int pi = 0; pi < MAX_PHOTOS; pi++)
        {
            ws.Column(THUMB_COL + pi).Width = 11; // thumbnail
            ws.Column(LINK_COL  + pi).Width = 10; // hyperlink
        }

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Seek(0, SeekOrigin.Begin);

        const string mime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        return File(ms, mime, "zaznamy-dochadzky.xlsx");
    }
}
