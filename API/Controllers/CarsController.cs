using API.Data;
using API.DTOs;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CarsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blobStorage;

    public CarsController(AppDbContext db, IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
    }

    private static CarDto ToDto(Car c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        LicensePlate = c.LicensePlate,
        PhotoUrl = c.PhotoUrl,
        Division = c.Division,
        IsActive = c.IsActive
    };

    [HttpGet]
    public async Task<ActionResult<List<CarDto>>> GetAll()
    {
        // Per-vehicle spending report (F1) — effective chain line ?? DL ??
        // document, grossed s DPH. See MachinesController.GetAll.
        var spend = await _db.MaterialPurchaseLines
            .Where(l => l.Purchase.InvoiceDocument != null
                     && l.Purchase.InvoiceDocument.Status != "discarded"
                     && l.Purchase.InvoiceDocument.Direction == "cost")
            .Select(l => new
            {
                CarId = l.CarId ?? l.Purchase.CarId ?? l.Purchase.InvoiceDocument!.CarId,
                Gross = l.LineTotal * (1 + l.VatRate / 100m)
            })
            .Where(x => x.CarId != null)
            .GroupBy(x => x.CarId!.Value)
            .Select(g => new { Id = g.Key, Sum = g.Sum(x => x.Gross) })
            .ToListAsync();
        var spendById = spend.ToDictionary(x => x.Id, x => Math.Round(x.Sum, 2, MidpointRounding.AwayFromZero));

        var cars = await _db.Cars.ToListAsync();
        return cars.Select(c => new CarDto
        {
            Id = c.Id,
            Name = c.Name,
            LicensePlate = c.LicensePlate,
            PhotoUrl = c.PhotoUrl,
            Division = c.Division,
            IsActive = c.IsActive,
            CostTotal = spendById.GetValueOrDefault(c.Id)
        }).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CarDto>> Get(int id)
    {
        var car = await _db.Cars.FindAsync(id);
        if (car == null) return NotFound();
        return ToDto(car);
    }

    /// <summary>
    /// Per-document cost ledger of this vehicle (F4 — servisy priradené ku
    /// konkrétnemu autu). Effective chain line ?? DL ?? document, grossed
    /// s DPH. Informational; division money computes on the division (D4).
    /// </summary>
    [HttpGet("{id}/costs")]
    public async Task<ActionResult<List<AssetCostDocDto>>> GetCosts(int id)
    {
        if (await _db.Cars.FindAsync(id) == null) return NotFound();

        var docs = await _db.MaterialPurchaseLines
            .Where(l => l.Purchase.InvoiceDocument != null
                     && l.Purchase.InvoiceDocument.Status != "discarded"
                     && l.Purchase.InvoiceDocument.Direction == "cost"
                     && (l.CarId ?? l.Purchase.CarId ?? l.Purchase.InvoiceDocument!.CarId) == id)
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
    public async Task<ActionResult<CarDto>> Create(CreateCarDto dto)
    {
        var car = new Car
        {
            Name = dto.Name,
            LicensePlate = dto.LicensePlate,
            Division = dto.Division == "stroje" ? "stroje" : "profistav"
        };
        _db.Cars.Add(car);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = car.Id }, ToDto(car));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateCarDto dto)
    {
        var car = await _db.Cars.FindAsync(id);
        if (car == null) return NotFound();
        car.Name = dto.Name;
        car.LicensePlate = dto.LicensePlate;
        if (dto.Division is "profistav" or "stroje")
            car.Division = dto.Division;
        car.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/toggle-active")]
    public async Task<ActionResult> ToggleActive(int id)
    {
        var car = await _db.Cars.FindAsync(id);
        if (car == null) return NotFound();
        car.IsActive = !car.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/photo")]
    public async Task<ActionResult<string>> UploadPhoto(int id, IFormFile file)
    {
        var car = await _db.Cars.FindAsync(id);
        if (car == null) return NotFound();

        if (file.Length == 0 || file.Length > 5 * 1024 * 1024)
            return BadRequest("File must be between 1 byte and 5MB");

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return BadRequest("Only image files (jpg, png, gif, webp) are allowed");

        if (_blobStorage == null)
            return StatusCode(503, "Photo storage is not configured");

        if (!string.IsNullOrEmpty(car.PhotoUrl))
            await _blobStorage.DeleteAsync(car.PhotoUrl, "car-photos");

        using var stream = file.OpenReadStream();
        car.PhotoUrl = await _blobStorage.UploadAsync(stream, file.FileName, "car-photos");
        await _db.SaveChangesAsync();

        return Ok(car.PhotoUrl);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var car = await _db.Cars.Include(c => c.TimeEntries).FirstOrDefaultAsync(c => c.Id == id);
        if (car == null) return NotFound();

        if (!string.IsNullOrEmpty(car.PhotoUrl) && _blobStorage != null)
            await _blobStorage.DeleteAsync(car.PhotoUrl, "car-photos");

        foreach (var te in car.TimeEntries) te.CarId = null;
        _db.Cars.Remove(car);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
