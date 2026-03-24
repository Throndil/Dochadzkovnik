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
public class EmployeesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPinHasher _pinHasher;
    private readonly IBlobStorageService? _blobStorage;
    private readonly IConfiguration _config;

    public EmployeesController(AppDbContext db, IPinHasher pinHasher,
         IConfiguration config, IBlobStorageService? blobStorage = null)
    {
        _db = db;
        _pinHasher = pinHasher;
        _blobStorage = blobStorage;
        _config = config;
    }

    [HttpGet]
    public async Task<ActionResult<List<EmployeeDto>>> GetAll()
    {
        return await _db.Employees
            .Select(e => new EmployeeDto
            {
                Id = e.Id,
                FirstName = e.FirstName,
                LastName = e.LastName,
                PhoneNumber = e.PhoneNumber,
                Address = e.Address,
                City = e.City,
                PhotoUrl = e.PhotoUrl,
                IsActive = e.IsActive,
                CreatedAt = e.CreatedAt
            })
            .ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EmployeeDto>> Get(int id)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        return new EmployeeDto
        {
            Id = emp.Id,
            FirstName = emp.FirstName,
            LastName = emp.LastName,
            PhoneNumber = emp.PhoneNumber,
            Address = emp.Address,
            City = emp.City,
            PhotoUrl = emp.PhotoUrl,
            IsActive = emp.IsActive,
            CreatedAt = emp.CreatedAt
        };
    }

    [HttpGet("generate-pin")]
    public async Task<ActionResult<object>> GeneratePin()
    {
        var allHashes = await _db.Employees.Select(e => e.Pin).ToListAsync();
        string pin;
        var attempts = 0;
        do
        {
            pin = Random.Shared.Next(1000, 10000).ToString();
            attempts++;
            if (attempts > 500)
                return StatusCode(503, "Nedá sa vygenerovať unikátny PIN.");
        } while (allHashes.Any(h => _pinHasher.Verify(h, pin)));

        return Ok(new { pin });
    }

    [HttpPost]
    public async Task<ActionResult<EmployeeDto>> Create(CreateEmployeeDto dto)
    {
        if (await IsPinTaken(dto.Pin))
            return Conflict("Tento PIN je už priradený inému zamestnancovi.");

        var employee = new Employee
        {
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Pin = _pinHasher.Hash(dto.Pin),
            PhoneNumber = dto.PhoneNumber,
            Address = dto.Address,
            City = dto.City
        };

        _db.Employees.Add(employee);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = employee.Id }, new EmployeeDto
        {
            Id = employee.Id,
            FirstName = employee.FirstName,
            LastName = employee.LastName,
            IsActive = employee.IsActive,
            CreatedAt = employee.CreatedAt
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, UpdateEmployeeDto dto)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        emp.FirstName = dto.FirstName;
        emp.LastName = dto.LastName;
        emp.PhoneNumber = dto.PhoneNumber;
        emp.Address = dto.Address;
        emp.City = dto.City;
        emp.IsActive = dto.IsActive;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        emp.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/toggle-active")]
    public async Task<ActionResult> ToggleActive(int id)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        emp.IsActive = !emp.IsActive;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/pin")]
    public async Task<ActionResult> SetPin(int id, SetPinDto dto)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

        if (await IsPinTaken(dto.Pin, excludeEmployeeId: id))
            return Conflict("Tento PIN je už priradený inému zamestnancovi.");

        emp.Pin = _pinHasher.Hash(dto.Pin);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}/permanent")]
    public async Task<ActionResult> DeletePermanent(int id)
    {
        var emp = await _db.Employees
            .Include(e => e.TimeEntries)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (emp == null) return NotFound();

        _db.TimeEntries.RemoveRange(emp.TimeEntries);
        _db.Employees.Remove(emp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/photo")]
    public async Task<ActionResult<string>> UploadPhoto(int id, IFormFile file)
    {
        var emp = await _db.Employees.FindAsync(id);
        if (emp == null) return NotFound();

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

        if (!string.IsNullOrEmpty(emp.PhotoUrl))
        {
            var container = _config["AzureBlobStorage:EmployeePhotosContainer"] ?? "employee-photos";
            await _blobStorage.DeleteAsync(emp.PhotoUrl, container);
        }

        using var stream = file.OpenReadStream();
        var containerName = _config["AzureBlobStorage:EmployeePhotosContainer"] ?? "employee-photos";
        emp.PhotoUrl = await _blobStorage.UploadAsync(stream, file.FileName, containerName);
        await _db.SaveChangesAsync();

        return Ok(emp.PhotoUrl);
    }

    private async Task<bool> IsPinTaken(string pin, int? excludeEmployeeId = null)
    {
        var query = _db.Employees.AsQueryable();
        if (excludeEmployeeId.HasValue)
            query = query.Where(e => e.Id != excludeEmployeeId.Value);

        var hashes = await query.Select(e => e.Pin).ToListAsync();
        return hashes.Any(h => _pinHasher.Verify(h, pin));
    }
}
