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
        IsActive = c.IsActive
    };

    [HttpGet]
    public async Task<ActionResult<List<CarDto>>> GetAll()
    {
        return await _db.Cars
            .Select(c => new CarDto
            {
                Id = c.Id,
                Name = c.Name,
                LicensePlate = c.LicensePlate,
                PhotoUrl = c.PhotoUrl,
                IsActive = c.IsActive
            })
            .ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CarDto>> Get(int id)
    {
        var car = await _db.Cars.FindAsync(id);
        if (car == null) return NotFound();
        return ToDto(car);
    }

    [HttpPost]
    public async Task<ActionResult<CarDto>> Create(CreateCarDto dto)
    {
        var car = new Car { Name = dto.Name, LicensePlate = dto.LicensePlate };
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
