using API.Data;
using API.DTOs;
using API.Filters;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// Admin surface for material purchases — see MATERIAL_PURCHASES_PLAN.md.
/// Behind the MaterialPurchases feature flag (mirrors Notifications + Commander).
/// JWT-protected; the kiosk side lives in <see cref="KioskController"/>.
/// </summary>
[ApiController]
[Route("api/material-purchases")]
[Authorize]
[RequireFeatureOrSuperAdmin("MaterialPurchases")]
public class MaterialPurchasesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blobStorage;
    private readonly IMaterialPurchasesExcelExportService _excelExport;

    // Receipt photos go under their own Cloudinary folder, partitioned by year-month so
    // a per-month delete sweep stays trivial later (mirrors work-photos / material-photos).
    private const string ReceiptFolderRoot = "receipts";

    public MaterialPurchasesController(
        AppDbContext db,
        IMaterialPurchasesExcelExportService excelExport,
        IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
        _excelExport = excelExport;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Read
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// List purchases. All filters AND together. <paramref name="materialId"/> matches
    /// either a linked catalogue line OR — when the catalogue row's name is reused as
    /// the raw name on a still-unidentified line — the raw name itself, so the customer
    /// finds every "Cement" purchase regardless of promotion state.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MaterialPurchaseDto>>> List(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? locationId,
        [FromQuery] int? employeeId,
        [FromQuery] int? materialId,
        [FromQuery] string? supplier,
        [FromQuery] bool? inventoryOnly)
    {
        var q = _db.MaterialPurchases
            .Include(p => p.Employee)
            .Include(p => p.Location)
            .Include(p => p.Lines)
                .ThenInclude(l => l.Material)
            .AsQueryable();

        // Income invoices (AZ billed someone) and AZ Stroje division documents
        // (nafta, olej, štrky…) carry line rows but are NOT material
        // purchases; keep them out of every material view. Stroje costs live
        // on the division page + per-mašina report.
        q = q.Where(p => p.InvoiceDocument == null
                      || (p.InvoiceDocument.Direction != "income" && p.InvoiceDocument.Division != "stroje"));

        if (from.HasValue) q = q.Where(p => p.PurchaseDate >= from.Value.Date);
        if (to.HasValue)   q = q.Where(p => p.PurchaseDate <  to.Value.Date.AddDays(1));
        // Inventory tab — purchases with no target site (worker chose "Inventár" on the kiosk).
        if (inventoryOnly == true)  q = q.Where(p => p.LocationId == null);
        if (locationId.HasValue) q = q.Where(p => p.LocationId == locationId.Value);
        if (employeeId.HasValue) q = q.Where(p => p.EmployeeId == employeeId.Value);
        if (!string.IsNullOrWhiteSpace(supplier))
        {
            var sLower = supplier.Trim().ToLower();
            q = q.Where(p => p.SupplierName != null && p.SupplierName.ToLower().Contains(sLower));
        }
        if (materialId.HasValue)
        {
            // Match purchases that contain a line linked to this material id, OR a line
            // with a raw name matching the catalogue row's name (case-insensitive). This
            // means filtering by "Cement" surfaces both promoted and not-yet-promoted lines.
            var matName = await _db.Materials
                .Where(m => m.Id == materialId.Value)
                .Select(m => m.Name.ToLower())
                .FirstOrDefaultAsync();
            q = q.Where(p => p.Lines.Any(l =>
                l.MaterialId == materialId.Value
                || (matName != null && l.MaterialNameRaw.ToLower() == matName)));
        }

        var list = await q.OrderByDescending(p => p.PurchaseDate).ToListAsync();
        return list.Select(ToDto).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MaterialPurchaseDto>> Get(int id)
    {
        var purchase = await _db.MaterialPurchases
            .Include(p => p.Employee)
            .Include(p => p.Location)
            .Include(p => p.Lines)
                .ThenInclude(l => l.Material)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (purchase == null) return NotFound();
        return ToDto(purchase);
    }

    /// <summary>
    /// Returns one row per (raw name, unit) cluster of orphan lines (MaterialId == null).
    /// Drives the "Neidentifikované" admin tab — admin promotes a typo cluster in one click.
    /// </summary>
    [HttpGet("unknown-groups")]
    public async Task<ActionResult<List<UnknownMaterialGroupDto>>> UnknownGroups()
    {
        var orphanLines = await _db.MaterialPurchaseLines
            .Where(l => l.MaterialId == null)
            .Where(l => l.Purchase.InvoiceDocument == null
                     || (l.Purchase.InvoiceDocument.Direction != "income" && l.Purchase.InvoiceDocument.Division != "stroje"))
            .Include(l => l.Purchase)
                .ThenInclude(p => p.Employee)
            .ToListAsync();

        var groups = orphanLines
            .GroupBy(l => new
            {
                NameKey = l.MaterialNameRaw.Trim().ToLower(),
                UnitKey = l.Unit.Trim().ToLower()
            })
            .Select(g => new UnknownMaterialGroupDto
            {
                MaterialNameRaw = g.OrderBy(l => l.CreatedAt).First().MaterialNameRaw,
                Unit            = g.OrderBy(l => l.CreatedAt).First().Unit,
                LineCount       = g.Count(),
                TotalQuantity   = g.Sum(l => l.Quantity),
                // S DPH — invoice-derived lines grossed up, kiosk lines as paid.
                TotalSpend      = Math.Round(g.Sum(l => l.LineTotal * GrossFactor(l.Purchase, l)), 2, MidpointRounding.AwayFromZero),
                AverageUnitPrice = g.Sum(l => l.Quantity) == 0m
                    ? 0m
                    : g.Sum(l => l.LineTotal * GrossFactor(l.Purchase, l)) / g.Sum(l => l.Quantity),
                FirstSeenAt = g.Min(l => l.Purchase.PurchaseDate),
                LastSeenAt  = g.Max(l => l.Purchase.PurchaseDate),
                EnteredByEmployeeNames = g
                    .Select(l => $"{l.Purchase.Employee.FirstName} {l.Purchase.Employee.LastName}")
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList()
            })
            .OrderByDescending(g => g.LineCount)
            .ToList();

        return groups;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Excel export
    // ─────────────────────────────────────────────────────────────────

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? locationId,
        [FromQuery] int? employeeId,
        [FromQuery] int? materialId,
        [FromQuery] string? supplier,
        [FromQuery] bool? inventoryOnly)
    {
        // Reuse the list method's filtering by inlining the same query — keeps the export
        // exactly aligned with what the admin sees on the Nákupy / Inventár tab.
        var listResult = await List(from, to, locationId, employeeId, materialId, supplier, inventoryOnly);
        var purchases = listResult.Value ?? new List<MaterialPurchaseDto>();

        string? locName = locationId.HasValue
            ? await _db.Locations.Where(l => l.Id == locationId.Value).Select(l => l.Name).FirstOrDefaultAsync()
            : null;
        string? empName = employeeId.HasValue
            ? await _db.Employees.Where(e => e.Id == employeeId.Value)
                .Select(e => e.FirstName + " " + e.LastName).FirstOrDefaultAsync()
            : null;

        var bytes = _excelExport.BuildPurchasesReport(from, to, locName, empName, supplier, purchases);

        var fileName = "Nakupy"
            + (locName != null ? "_" + Sanitize(locName) : "")
            + "_" + (from?.ToString("yyyy-MM-dd") ?? "start")
            + "_" + (to?.ToString("yyyy-MM-dd") ?? "now")
            + ".xlsx";

        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Create / Update / Delete (admin-side)
    // ─────────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<ActionResult<MaterialPurchaseDto>> Create([FromBody] CreateMaterialPurchaseDto dto)
    {
        if (dto.Lines == null || dto.Lines.Count == 0)
            return BadRequest("Pridaj aspoň jednu položku.");

        var employee = await _db.Employees.FindAsync(dto.EmployeeId);
        if (employee == null) return BadRequest("Neplatný zamestnanec.");

        if (dto.LocationId.HasValue)
        {
            var loc = await _db.Locations.FindAsync(dto.LocationId.Value);
            if (loc == null) return BadRequest("Neplatné pracovisko.");
        }

        if (dto.TimeEntryId.HasValue)
        {
            var te = await _db.TimeEntries.FindAsync(dto.TimeEntryId.Value);
            if (te == null) return BadRequest("Neplatný záznam dochádzky.");
        }

        var purchase = new MaterialPurchase
        {
            PurchaseDate = dto.PurchaseDate,
            EmployeeId   = dto.EmployeeId,
            LocationId   = dto.LocationId,
            TimeEntryId  = dto.TimeEntryId,
            SupplierName = string.IsNullOrWhiteSpace(dto.SupplierName) ? null : dto.SupplierName.Trim(),
            Note         = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim()
        };

        foreach (var l in dto.Lines)
        {
            var (line, err) = await TryBuildLineAsync(l);
            if (err != null) return BadRequest(err);
            purchase.Lines.Add(line!);
        }
        purchase.TotalCost = purchase.Lines.Sum(l => l.LineTotal);

        _db.MaterialPurchases.Add(purchase);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = purchase.Id }, ToDto(await ReloadAsync(purchase.Id)));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<MaterialPurchaseDto>> Update(int id, [FromBody] UpdateMaterialPurchaseDto dto)
    {
        if (dto.Lines == null || dto.Lines.Count == 0)
            return BadRequest("Pridaj aspoň jednu položku.");

        var purchase = await _db.MaterialPurchases
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (purchase == null) return NotFound();

        // Invoice-derived purchases mirror a scanned faktúra: their lines are
        // stored bez DPH and edited on the invoice review screen, while this
        // endpoint receives the grossed s-DPH display values — saving them
        // here would double-tax the lines. Route the manager to the right
        // editor instead.
        if (purchase.InvoiceDocumentId != null)
            return BadRequest("Tento nákup pochádza zo skenovanej faktúry — upravte ho v module Faktúry.");

        if (dto.LocationId.HasValue)
        {
            var loc = await _db.Locations.FindAsync(dto.LocationId.Value);
            if (loc == null) return BadRequest("Neplatné pracovisko.");
        }

        // Reconcile lines: incoming rows with Id update existing; without Id insert new;
        // existing rows not present in the incoming set are deleted. Mirror what the
        // admin edit drawer expects (replace the whole lines collection).
        var keepIds = dto.Lines.Where(l => l.Id.HasValue).Select(l => l.Id!.Value).ToHashSet();
        var toRemove = purchase.Lines.Where(l => !keepIds.Contains(l.Id)).ToList();

        foreach (var l in dto.Lines)
        {
            if (l.Id.HasValue)
            {
                var existing = purchase.Lines.FirstOrDefault(x => x.Id == l.Id.Value);
                if (existing == null) return BadRequest($"Položka {l.Id} nepatrí k tomuto nákupu.");
                var err = await TryUpdateLineAsync(existing, l);
                if (err != null) return BadRequest(err);
            }
            else
            {
                var (line, err) = await TryBuildLineAsync(new CreateMaterialPurchaseLineDto
                {
                    MaterialId      = l.MaterialId,
                    MaterialNameRaw = l.MaterialNameRaw,
                    Unit            = l.Unit,
                    Quantity        = l.Quantity,
                    UnitPrice       = l.UnitPrice
                });
                if (err != null) return BadRequest(err);
                purchase.Lines.Add(line!);
            }
        }

        // Apply the deletes after validation has passed for everything else, so a
        // mid-loop validation failure cannot leave a half-mutated tracker.
        foreach (var removed in toRemove) _db.MaterialPurchaseLines.Remove(removed);

        // Header fields (mutated last so an early return doesn't poison state).
        purchase.PurchaseDate = dto.PurchaseDate;
        purchase.LocationId   = dto.LocationId;
        purchase.SupplierName = string.IsNullOrWhiteSpace(dto.SupplierName) ? null : dto.SupplierName.Trim();
        purchase.Note         = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim();

        // After Add + Update + Remove: purchase.Lines still contains the to-be-deleted rows
        // (EF's tracker marks them for delete, the navigation collection is kept). Sum over
        // the survivors only — the kept ones already have their refreshed LineTotal from
        // TryUpdateLineAsync, the new ones have LineTotal set by TryBuildLineAsync.
        purchase.TotalCost = purchase.Lines.Where(x => !toRemove.Contains(x)).Sum(x => x.LineTotal);

        await _db.SaveChangesAsync();
        return ToDto(await ReloadAsync(purchase.Id));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var purchase = await _db.MaterialPurchases
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (purchase == null) return NotFound();

        // Receipt photo first (best effort — Cloudinary outage shouldn't block local deletion).
        if (!string.IsNullOrEmpty(purchase.ReceiptPhotoUrl) && _blobStorage != null)
        {
            try { await _blobStorage.DeleteAsync(purchase.ReceiptPhotoUrl, ReceiptFolderRoot); } catch { }
        }

        // Lines cascade via the FK ON DELETE CASCADE; explicit removal is optional but
        // keeps EF's tracker in a sane state.
        _db.MaterialPurchaseLines.RemoveRange(purchase.Lines);
        _db.MaterialPurchases.Remove(purchase);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────
    //  Receipt photo
    // ─────────────────────────────────────────────────────────────────

    [HttpPost("{id}/photo")]
    public async Task<ActionResult<string>> UploadReceipt(int id, IFormFile file)
    {
        var purchase = await _db.MaterialPurchases.FindAsync(id);
        if (purchase == null) return NotFound();

        if (file == null || file.Length == 0 || file.Length > 10 * 1024 * 1024)
            return BadRequest("Súbor musí byť medzi 1 B a 10 MB.");
        if (_blobStorage == null)
            return StatusCode(503, "Úložisko fotografií nie je nakonfigurované.");

        // Replace, don't append — one receipt per purchase.
        if (!string.IsNullOrEmpty(purchase.ReceiptPhotoUrl))
        {
            try { await _blobStorage.DeleteAsync(purchase.ReceiptPhotoUrl, ReceiptFolderRoot); } catch { }
        }

        using var stream = file.OpenReadStream();
        var folder = $"{ReceiptFolderRoot}/{purchase.PurchaseDate:yyyy-MM}";
        purchase.ReceiptPhotoUrl = await _blobStorage.UploadAsync(stream, file.FileName, folder);
        await _db.SaveChangesAsync();

        return Ok(purchase.ReceiptPhotoUrl);
    }

    [HttpDelete("{id}/photo")]
    public async Task<ActionResult> DeleteReceipt(int id)
    {
        var purchase = await _db.MaterialPurchases.FindAsync(id);
        if (purchase == null) return NotFound();
        if (string.IsNullOrEmpty(purchase.ReceiptPhotoUrl)) return NoContent();

        if (_blobStorage != null)
        {
            try { await _blobStorage.DeleteAsync(purchase.ReceiptPhotoUrl, ReceiptFolderRoot); } catch { }
        }
        purchase.ReceiptPhotoUrl = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────
    //  Promote a free-typed line into the catalogue
    // ─────────────────────────────────────────────────────────────────

    [HttpPost("{purchaseId}/lines/{lineId}/promote")]
    public async Task<ActionResult<MaterialPurchasePromoteResultDto>> PromoteLine(
        int purchaseId, int lineId, [FromBody] PromoteMaterialLineDto dto)
    {
        var line = await _db.MaterialPurchaseLines
            .FirstOrDefaultAsync(l => l.Id == lineId && l.PurchaseId == purchaseId);
        if (line == null) return NotFound();

        Material? target = null;
        bool createdNew = false;

        if (string.Equals(dto.Mode, "merge", StringComparison.OrdinalIgnoreCase))
        {
            if (!dto.CatalogueMaterialId.HasValue)
                return BadRequest("Chýba CatalogueMaterialId.");
            target = await _db.Materials.FindAsync(dto.CatalogueMaterialId.Value);
            if (target == null) return BadRequest("Neplatný materiál.");
            // Re-activate if soft-deleted so the merged-into row appears in dropdowns again.
            if (!target.IsActive) target.IsActive = true;
        }
        else if (string.Equals(dto.Mode, "new", StringComparison.OrdinalIgnoreCase))
        {
            var name = (dto.NewName ?? line.MaterialNameRaw).Trim();
            var unit = (dto.NewUnit ?? line.Unit).Trim();
            if (string.IsNullOrEmpty(name)) return BadRequest("Názov materiálu je povinný.");
            if (string.IsNullOrEmpty(unit)) return BadRequest("Jednotka je povinná.");

            // If a catalogue row with this name already exists (case-insensitive),
            // mirror MaterialsController.Create's resurrection behaviour: merge into
            // it (and reactivate if soft-deleted) instead of returning a 409.
            var lower = name.ToLower();
            var existing = await _db.Materials.FirstOrDefaultAsync(m => m.Name.ToLower() == lower);
            if (existing != null)
            {
                if (!existing.IsActive) existing.IsActive = true;
                // Don't silently overwrite Unit / PricePerUnit on a name-collision merge —
                // the admin can do that explicitly via the Katalóg tab if they want to.
                target = existing;
            }
            else
            {
                target = new Material
                {
                    Name = name,
                    Unit = unit,
                    PricePerUnit = dto.NewPricePerUnit ?? line.UnitPrice,
                    IsActive = true
                };
                _db.Materials.Add(target);
                createdNew = true;
            }
        }
        else
        {
            return BadRequest("Mode musí byť \"new\" alebo \"merge\".");
        }

        // Save first if we just inserted a new Material so we have its Id.
        if (createdNew) await _db.SaveChangesAsync();

        line.MaterialId = target!.Id;

        // Bulk-apply across same-raw-name + same-unit lines if asked. Default true so the
        // common case (one click cleans up all "Cemnt" typos at once) is the easy path.
        var linkedCount = 1;
        if (dto.ApplyToAllMatchingRawName)
        {
            var rawLower  = (line.MaterialNameRaw ?? string.Empty).Trim().ToLower();
            var unitLower = (line.Unit ?? string.Empty).Trim().ToLower();
            var siblings = await _db.MaterialPurchaseLines
                .Where(l =>
                    l.Id != line.Id
                    && l.MaterialId == null
                    && l.MaterialNameRaw.ToLower() == rawLower
                    && l.Unit.ToLower() == unitLower)
                .ToListAsync();
            foreach (var s in siblings) s.MaterialId = target.Id;
            linkedCount += siblings.Count;
        }

        await _db.SaveChangesAsync();

        return new MaterialPurchasePromoteResultDto
        {
            MaterialId = target.Id,
            MaterialName = target.Name,
            LinesLinked = linkedCount,
            CreatedNewCatalogueRow = createdNew
        };
    }

    // ─────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────

    private static decimal RoundLine(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Validates a line DTO and builds a <see cref="MaterialPurchaseLine"/>. The caller is
    /// responsible for appending it to the purchase's Lines collection. Returns
    /// (line, null) on success or (null, error) — never both.
    /// Snapshots the catalogue Unit when MaterialId is provided so admin renames don't
    /// rewrite history; otherwise uses the raw values from the DTO.
    /// </summary>
    private async Task<(MaterialPurchaseLine? line, string? error)> TryBuildLineAsync(CreateMaterialPurchaseLineDto dto)
    {
        var nameRaw = (dto.MaterialNameRaw ?? string.Empty).Trim();
        var unit    = (dto.Unit ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(nameRaw)) return (null, "Názov materiálu je povinný.");
        if (string.IsNullOrEmpty(unit))    return (null, "Jednotka je povinná.");

        if (dto.MaterialId.HasValue)
        {
            var mat = await _db.Materials.FindAsync(dto.MaterialId.Value);
            if (mat == null) return (null, "Neplatný materiál.");
            // When the admin explicitly links a catalogue row, snapshot the catalogue Unit
            // so a future rename of the catalogue row does not retroactively change this line.
            unit = mat.Unit;
            // Keep MaterialNameRaw as the catalogue's current Name when blank —
            // makes the audit trail readable. Otherwise respect what the admin sent.
            if (string.IsNullOrWhiteSpace(nameRaw)) nameRaw = mat.Name;
        }

        var line = new MaterialPurchaseLine
        {
            MaterialId      = dto.MaterialId,
            MaterialNameRaw = nameRaw,
            Unit            = unit,
            Quantity        = dto.Quantity,
            UnitPrice       = dto.UnitPrice,
            LineTotal       = RoundLine(dto.Quantity * dto.UnitPrice)
        };
        return (line, null);
    }

    /// <summary>
    /// Mutates an existing tracked line from the update DTO. Returns null on success
    /// or an error string for the caller to surface as 400.
    /// </summary>
    private async Task<string?> TryUpdateLineAsync(MaterialPurchaseLine existing, UpdateMaterialPurchaseLineDto dto)
    {
        var nameRaw = (dto.MaterialNameRaw ?? string.Empty).Trim();
        var unit    = (dto.Unit ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(nameRaw)) return "Názov materiálu je povinný.";
        if (string.IsNullOrEmpty(unit))    return "Jednotka je povinná.";

        if (dto.MaterialId.HasValue)
        {
            var mat = await _db.Materials.FindAsync(dto.MaterialId.Value);
            if (mat == null) return "Neplatný materiál.";
            unit = mat.Unit;
        }

        existing.MaterialId      = dto.MaterialId;
        existing.MaterialNameRaw = nameRaw;
        existing.Unit            = unit;
        existing.Quantity        = dto.Quantity;
        existing.UnitPrice       = dto.UnitPrice;
        existing.LineTotal       = RoundLine(dto.Quantity * dto.UnitPrice);
        return null;
    }

    private async Task<MaterialPurchase> ReloadAsync(int id)
    {
        return (await _db.MaterialPurchases
            .Include(p => p.Employee)
            .Include(p => p.Location)
            .Include(p => p.Lines)
                .ThenInclude(l => l.Material)
            .FirstOrDefaultAsync(p => p.Id == id))!;
    }

    /// <summary>
    /// Accounting rule (customer): everything the manager reads is S DPH.
    /// Kiosk/admin-entered lines already store what the worker paid (s DPH,
    /// VatRate is just the 23 % back-compat default). Invoice-derived lines
    /// store bez DPH (the reconciliation gate needs the printed values), so
    /// ONLY those get grossed up here — at read time, storage untouched.
    /// </summary>
    private static decimal GrossFactor(MaterialPurchase p, MaterialPurchaseLine l)
        => p.InvoiceDocumentId != null ? 1m + l.VatRate / 100m : 1m;

    private static MaterialPurchaseDto ToDto(MaterialPurchase p) => new()
    {
        Id              = p.Id,
        PurchaseDate    = p.PurchaseDate,
        EmployeeId      = p.EmployeeId,
        EmployeeName    = p.Employee == null ? string.Empty : $"{p.Employee.FirstName} {p.Employee.LastName}",
        LocationId      = p.LocationId,
        LocationName    = p.Location?.Name,
        TimeEntryId     = p.TimeEntryId,
        InvoiceDocumentId = p.InvoiceDocumentId,
        SupplierName    = p.SupplierName,
        ReceiptPhotoUrl = p.ReceiptPhotoUrl,
        Note            = p.Note,
        TotalCost       = Math.Round(p.Lines.Sum(l => l.LineTotal * GrossFactor(p, l)), 2, MidpointRounding.AwayFromZero),
        CreatedAt       = p.CreatedAt,
        UpdatedAt       = p.UpdatedAt,
        Lines = p.Lines.OrderBy(l => l.Id).Select(l => new MaterialPurchaseLineDto
        {
            Id              = l.Id,
            MaterialId      = l.MaterialId,
            MaterialName    = l.Material?.Name,
            MaterialNameRaw = l.MaterialNameRaw,
            Unit            = l.Unit,
            Quantity        = l.Quantity,
            UnitPrice       = Math.Round(l.UnitPrice * GrossFactor(p, l), 4, MidpointRounding.AwayFromZero),
            LineTotal       = Math.Round(l.LineTotal * GrossFactor(p, l), 2, MidpointRounding.AwayFromZero)
        }).ToList()
    };

    /// <summary>
    /// Strips Slovak diacritics + spaces for use in filename suffixes.
    /// Mirrors the convention from <see cref="MaterialExcelExportService"/>.
    /// </summary>
    private static string Sanitize(string s)
    {
        var normalized = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return sb.ToString().Replace(' ', '_');
    }
}
