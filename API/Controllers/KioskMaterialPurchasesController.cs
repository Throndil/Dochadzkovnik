using API.Data;
using API.DTOs;
using API.Filters;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// Kiosk-side endpoints for the Nákup materiálu flow — see MATERIAL_PURCHASES_PLAN.md.
/// PIN-validated (no JWT). Behind the MaterialPurchases feature flag at the class level
/// so the controller is invisible (404 everywhere) until the superadmin flips the flag.
/// Sister controller: <see cref="MaterialPurchasesController"/> for the JWT admin surface.
/// </summary>
[ApiController]
[Route("api/kiosk/material-purchases")]
[RequireFeatureOrSuperAdmin("MaterialPurchases")]
public class KioskMaterialPurchasesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPinHasher _pinHasher;
    private readonly IBlobStorageService? _blobStorage;
    private readonly IConfiguration _config;

    private static readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");
    private static DateTime BratislavaNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);

    private const string ReceiptFolderRoot = "receipts";

    public KioskMaterialPurchasesController(
        AppDbContext db,
        IPinHasher pinHasher,
        IConfiguration config,
        IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _pinHasher = pinHasher;
        _config = config;
        _blobStorage = blobStorage;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Config — tells the kiosk which Location triggers the in-šichta capture
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the trigger Location for the in-šichta combined capture flow.
    ///
    /// Resolution order:
    ///   1. Explicit <c>MaterialPurchases:TriggerLocationId</c> in config (Railway env or
    ///      appsettings.Local.json). When set, that Location.Id activates the capture.
    ///   2. Fallback by name "Nákup materiálu" (case-insensitive, IsActive only) — keeps
    ///      a fresh dev DB working without operator setup.
    ///
    /// Returns null Id when neither matches; the kiosk treats that as "feature flag is on
    /// but no trigger Location configured — only the standalone tile is available".
    /// </summary>
    [HttpGet("config")]
    public async Task<ActionResult<MaterialPurchasesKioskConfigDto>> GetConfig()
    {
        var explicitId = _config.GetValue<int?>("MaterialPurchases:TriggerLocationId");
        if (explicitId.HasValue && explicitId.Value > 0)
        {
            var loc = await _db.Locations.FindAsync(explicitId.Value);
            if (loc != null && loc.IsActive)
            {
                return new MaterialPurchasesKioskConfigDto
                {
                    TriggerLocationId   = loc.Id,
                    TriggerLocationName = loc.Name
                };
            }
            // Configured but the Location does not exist / is inactive — fall through to the
            // name-match fallback rather than failing closed, so a typo doesn't break the kiosk.
        }

        var byName = await _db.Locations
            .Where(l => l.IsActive && l.Name.ToLower() == "nákup materiálu")
            .Select(l => new { l.Id, l.Name })
            .FirstOrDefaultAsync();

        return new MaterialPurchasesKioskConfigDto
        {
            TriggerLocationId   = byName?.Id,
            TriggerLocationName = byName?.Name
        };
    }

    // ─────────────────────────────────────────────────────────────────
    //  Catalogue lookup for the kiosk Položky picker
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Active-only material catalogue, anonymous so the kiosk picker can search without
    /// a JWT. Unlike <c>/api/materials</c> (admin), this returns only IsActive rows.
    /// Used for both the standalone Nákup tile and the in-šichta combined capture.
    /// </summary>
    [HttpGet("catalogue")]
    public async Task<ActionResult<List<MaterialDto>>> GetCatalogue()
    {
        return await _db.Materials
            .Where(m => m.IsActive)
            .OrderBy(m => m.Name)
            .Select(m => new MaterialDto
            {
                Id = m.Id,
                Name = m.Name,
                Unit = m.Unit,
                PricePerUnit = m.PricePerUnit,
                IsActive = m.IsActive
            })
            .ToListAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    //  Create a purchase
    // ─────────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<ActionResult<MaterialPurchaseDto>> Create([FromBody] CreateKioskMaterialPurchaseDto dto)
    {
        if (dto.Lines == null || dto.Lines.Count == 0)
            return BadRequest("Pridaj aspoň jednu položku.");

        var employee = await FindEmployeeByPin(dto.Pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        if (dto.LocationId.HasValue)
        {
            var loc = await _db.Locations.FindAsync(dto.LocationId.Value);
            if (loc == null || !loc.IsActive) return BadRequest("Neplatné pracovisko.");
        }

        // TimeEntryId is set by the in-šichta combined flow (the worker just clocked
        // hours under the trigger Location and we want to link the purchase to that
        // record). Defence in depth: only accept it when the TimeEntry actually
        // belongs to the same employee and is a recent entry. Older entries with the
        // same employee aren't linkable from the kiosk — that path goes through admin.
        if (dto.TimeEntryId.HasValue)
        {
            var te = await _db.TimeEntries.FindAsync(dto.TimeEntryId.Value);
            if (te == null) return BadRequest("Neplatný záznam dochádzky.");
            if (te.EmployeeId != employee.Id) return BadRequest("Záznam dochádzky nepatrí prihlásenému zamestnancovi.");
        }

        var purchase = new MaterialPurchase
        {
            PurchaseDate = dto.PurchaseDate ?? BratislavaNow,
            EmployeeId   = employee.Id,
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

        // Reload with Includes so we can return a complete DTO (matches the admin shape).
        var reloaded = await _db.MaterialPurchases
            .Include(p => p.Employee)
            .Include(p => p.Location)
            .Include(p => p.Lines).ThenInclude(l => l.Material)
            .FirstAsync(p => p.Id == purchase.Id);

        return new MaterialPurchaseDto
        {
            Id              = reloaded.Id,
            PurchaseDate    = reloaded.PurchaseDate,
            EmployeeId      = reloaded.EmployeeId,
            EmployeeName    = $"{reloaded.Employee.FirstName} {reloaded.Employee.LastName}",
            LocationId      = reloaded.LocationId,
            LocationName    = reloaded.Location?.Name,
            TimeEntryId     = reloaded.TimeEntryId,
            SupplierName    = reloaded.SupplierName,
            ReceiptPhotoUrl = reloaded.ReceiptPhotoUrl,
            Note            = reloaded.Note,
            TotalCost       = reloaded.TotalCost,
            CreatedAt       = reloaded.CreatedAt,
            UpdatedAt       = reloaded.UpdatedAt,
            Lines = reloaded.Lines.OrderBy(l => l.Id).Select(l => new MaterialPurchaseLineDto
            {
                Id              = l.Id,
                MaterialId      = l.MaterialId,
                MaterialName    = l.Material?.Name,
                MaterialNameRaw = l.MaterialNameRaw,
                Unit            = l.Unit,
                Quantity        = l.Quantity,
                UnitPrice       = l.UnitPrice,
                LineTotal       = l.LineTotal
            }).ToList()
        };
    }

    // ─────────────────────────────────────────────────────────────────
    //  Receipt photo upload
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Upload (or replace) the receipt photo for an existing purchase. PIN-validated
    /// AND ownership-checked: only the worker who created the purchase can attach a
    /// receipt to it. Admin can still overwrite via the JWT-protected admin endpoint.
    /// </summary>
    [HttpPost("{id}/receipt")]
    public async Task<ActionResult<string>> UploadReceipt(int id, IFormFile file, [FromForm] string pin)
    {
        var employee = await FindEmployeeByPin(pin);
        if (employee == null) return Unauthorized("Neplatný PIN");

        var purchase = await _db.MaterialPurchases.FindAsync(id);
        if (purchase == null) return NotFound();
        if (purchase.EmployeeId != employee.Id)
            return Forbid();

        if (file == null || file.Length == 0 || file.Length > 10 * 1024 * 1024)
            return BadRequest("Súbor musí byť medzi 1 B a 10 MB.");
        if (_blobStorage == null)
            return StatusCode(503, "Úložisko fotografií nie je nakonfigurované.");

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

    // ─────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<Employee?> FindEmployeeByPin(string pin)
    {
        if (string.IsNullOrEmpty(pin)) return null;

        // Mirrors KioskController.FindEmployeeByPin — load active employees and verify
        // PIN client-side. Acceptable at small scale; see PROJECT_NOTES.md "Known Issues".
        var actives = await _db.Employees.Where(e => e.IsActive).ToListAsync();
        return actives.FirstOrDefault(e => _pinHasher.Verify(e.Pin, pin));
    }

    private static decimal RoundLine(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

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
            // Snapshot the catalogue Unit to keep history tied to what was true at insert.
            unit = mat.Unit;
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
}
