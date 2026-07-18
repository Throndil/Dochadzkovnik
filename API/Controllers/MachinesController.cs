using API.Data;
using API.DTOs;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// Machine registry of the AZ Stroje division (Fáza F0) — mirrors
/// <see cref="CarsController"/>: CRUD + photo + soft deactivate. The kiosk
/// Auto/Stroj/Pešo choice (F3) and the optional cost backtrack (F1) point
/// here; division money never computes on the machine (D4).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MachinesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blobStorage;

    public MachinesController(AppDbContext db, IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
    }

    private static MachineDto ToDto(Machine m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Note = m.Note,
        PhotoUrl = m.PhotoUrl,
        IsActive = m.IsActive
    };

    [HttpGet]
    public async Task<ActionResult<List<MachineDto>>> GetAll()
    {
        // Per-mašina spending report (F1): EFFECTIVE asset of a line =
        // line override ?? delivery list ?? document tag — same chain as
        // pracoviská. Grossed s DPH (accounting rule B2). Informational
        // only; division money computes on the division (D4).
        var spend = await _db.MaterialPurchaseLines
            .Where(l => l.Purchase.InvoiceDocument != null
                     && l.Purchase.InvoiceDocument.Status != "discarded"
                     && l.Purchase.InvoiceDocument.Direction == "cost")
            .Select(l => new
            {
                MachineId = l.MachineId ?? l.Purchase.MachineId ?? l.Purchase.InvoiceDocument!.MachineId,
                Gross = l.LineTotal * (1 + l.VatRate / 100m)
            })
            .Where(x => x.MachineId != null)
            .GroupBy(x => x.MachineId!.Value)
            .Select(g => new { Id = g.Key, Sum = g.Sum(x => x.Gross) })
            .ToListAsync();
        var spendById = spend.ToDictionary(x => x.Id, x => Math.Round(x.Sum, 2, MidpointRounding.AwayFromZero));

        var machines = await _db.Machines.OrderBy(m => m.Name).ToListAsync();
        return machines.Select(m => new MachineDto
        {
            Id = m.Id,
            Name = m.Name,
            Note = m.Note,
            PhotoUrl = m.PhotoUrl,
            IsActive = m.IsActive,
            CostTotal = spendById.GetValueOrDefault(m.Id)
        }).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MachineDto>> Get(int id)
    {
        var machine = await _db.Machines.FindAsync(id);
        if (machine == null) return NotFound();
        return ToDto(machine);
    }

    /// <summary>
    /// Per-document cost ledger of this mašina (F4 mirror of
    /// CarsController.GetCosts). Effective chain line ?? DL ?? document,
    /// grossed s DPH. Informational only (D4).
    /// </summary>
    [HttpGet("{id}/costs")]
    public async Task<ActionResult<List<AssetCostDocDto>>> GetCosts(int id)
    {
        if (await _db.Machines.FindAsync(id) == null) return NotFound();

        var docs = await _db.MaterialPurchaseLines
            .Where(l => l.Purchase.InvoiceDocument != null
                     && l.Purchase.InvoiceDocument.Status != "discarded"
                     && l.Purchase.InvoiceDocument.Direction == "cost"
                     && (l.MachineId ?? l.Purchase.MachineId ?? l.Purchase.InvoiceDocument!.MachineId) == id)
            .GroupBy(l => new
            {
                l.Purchase.InvoiceDocument!.Id,
                l.Purchase.InvoiceDocument!.InvoiceNumber,
                l.Purchase.InvoiceDocument!.SupplierName,
                l.Purchase.InvoiceDocument!.IssueDate,
                l.Purchase.InvoiceDocument!.DocumentKind,
                l.Purchase.InvoiceDocument!.Status
            })
            .Select(g => new AssetCostDocDto
            {
                InvoiceDocumentId = g.Key.Id,
                InvoiceNumber = g.Key.InvoiceNumber,
                SupplierName = g.Key.SupplierName,
                IssueDate = g.Key.IssueDate,
                DocumentKind = g.Key.DocumentKind,
                Status = g.Key.Status,
                GrossTotal = g.Sum(l => l.LineTotal * (1 + l.VatRate / 100m))
            })
            .OrderByDescending(d => d.IssueDate)
            .ToListAsync();

        foreach (var d in docs)
            d.GrossTotal = Math.Round(d.GrossTotal, 2, MidpointRounding.AwayFromZero);
        return docs;
    }

    [HttpPost]
    public async Task<ActionResult<MachineDto>> Create(CreateMachineDto dto)
    {
        var machine = new Machine { Name = dto.Name, Note = dto.Note };
        _db.Machines.Add(machine);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = machine.Id }, ToDto(machine));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateMachineDto dto)
    {
        var machine = await _db.Machines.FindAsync(id);
        if (machine == null) return NotFound();
        machine.Name = dto.Name;
        machine.Note = dto.Note;
        machine.IsActive = dto.IsActive;
        machine.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/toggle-active")]
    public async Task<ActionResult> ToggleActive(int id)
    {
        var machine = await _db.Machines.FindAsync(id);
        if (machine == null) return NotFound();
        machine.IsActive = !machine.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/photo")]
    public async Task<ActionResult<string>> UploadPhoto(int id, IFormFile file)
    {
        var machine = await _db.Machines.FindAsync(id);
        if (machine == null) return NotFound();

        if (file.Length == 0 || file.Length > 5 * 1024 * 1024)
            return BadRequest("File must be between 1 byte and 5MB");

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return BadRequest("Only image files (jpg, png, gif, webp) are allowed");

        if (_blobStorage == null)
            return StatusCode(503, "Photo storage is not configured");

        if (!string.IsNullOrEmpty(machine.PhotoUrl))
            await _blobStorage.DeleteAsync(machine.PhotoUrl, "machine-photos");

        using var stream = file.OpenReadStream();
        machine.PhotoUrl = await _blobStorage.UploadAsync(stream, file.FileName, "machine-photos");
        await _db.SaveChangesAsync();

        return Ok(machine.PhotoUrl);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var machine = await _db.Machines.Include(m => m.TimeEntries).FirstOrDefaultAsync(m => m.Id == id);
        if (machine == null) return NotFound();

        if (!string.IsNullOrEmpty(machine.PhotoUrl) && _blobStorage != null)
            await _blobStorage.DeleteAsync(machine.PhotoUrl, "machine-photos");

        foreach (var te in machine.TimeEntries) te.MachineId = null;
        _db.Machines.Remove(machine);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
