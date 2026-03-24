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
public class LocationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blobStorage;
    private readonly IConfiguration _config;

    public LocationsController(AppDbContext db,  IConfiguration config, IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _blobStorage = blobStorage;
        _config = config;
    }

    [HttpGet]
    public async Task<ActionResult<List<LocationDto>>> GetAll()
    {
        return await _db.Locations
            .Select(l => new LocationDto
            {
                Id = l.Id,
                Name = l.Name,
                Address = l.Address,
                PhotoUrl = l.PhotoUrl,
                IsActive = l.IsActive
            })
            .ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<LocationDto>> Get(int id)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        return new LocationDto
        {
            Id = loc.Id,
            Name = loc.Name,
            Address = loc.Address,
            PhotoUrl = loc.PhotoUrl,
            IsActive = loc.IsActive
        };
    }

    [HttpPost]
    public async Task<ActionResult<LocationDto>> Create(CreateLocationDto dto)
    {
        var location = new Location
        {
            Name = dto.Name,
            Address = dto.Address
        };

        _db.Locations.Add(location);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = location.Id }, new LocationDto
        {
            Id = location.Id,
            Name = location.Name,
            Address = location.Address,
            IsActive = location.IsActive
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateLocationDto dto)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        loc.Name = dto.Name;
        loc.Address = dto.Address;
        loc.IsActive = dto.IsActive;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        loc.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/toggle-active")]
    public async Task<ActionResult> ToggleActive(int id)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        loc.IsActive = !loc.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}/permanent")]
    public async Task<ActionResult> DeletePermanent(int id)
    {
        var loc = await _db.Locations
            .Include(l => l.TimeEntries)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (loc == null) return NotFound();

        if (!string.IsNullOrEmpty(loc.PhotoUrl) && _blobStorage != null)
        {
            var container = _config["AzureBlobStorage:LocationPhotosContainer"] ?? "location-photos";
            await _blobStorage.DeleteAsync(loc.PhotoUrl, container);
        }

        _db.TimeEntries.RemoveRange(loc.TimeEntries);
        _db.Locations.Remove(loc);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/photo")]
    public async Task<ActionResult<string>> UploadPhoto(int id, IFormFile file)
    {
        var loc = await _db.Locations.FindAsync(id);
        if (loc == null) return NotFound();

        if (file.Length == 0 || file.Length > 5 * 1024 * 1024)
            return BadRequest("File must be between 1 byte and 5MB");

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return BadRequest("Only image files (jpg, png, gif, webp) are allowed");

        if (_blobStorage == null)
        {
            return StatusCode(503, "Not configured photo service.");
        }

        if (!string.IsNullOrEmpty(loc.PhotoUrl))
        {
            var container = _config["AzureBlobStorage:LocationPhotosContainer"] ?? "location-photos";
            await _blobStorage.DeleteAsync(loc.PhotoUrl, container);
        }

        using var stream = file.OpenReadStream();
        var containerName = _config["AzureBlobStorage:LocationPhotosContainer"] ?? "location-photos";
        loc.PhotoUrl = await _blobStorage.UploadAsync(stream, file.FileName, containerName);
        await _db.SaveChangesAsync();

        return Ok(loc.PhotoUrl);
    }
}
