using API.Data;
using API.DTOs;
using API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// Palivové karty (F6) — registry of the company's fuel cards with the
/// current holder. Cards may be unassigned (holder not in the system yet).
/// Manager-only; managed on /admin/palivove-karty.
/// </summary>
[ApiController]
[Route("api/fuel-cards")]
[Authorize]
public class FuelCardsController : ControllerBase
{
    private readonly AppDbContext _db;

    public FuelCardsController(AppDbContext db) => _db = db;

    private static FuelCardDto ToDto(FuelCard c) => new()
    {
        Id = c.Id,
        Label = c.Label,
        Note = c.Note,
        EmployeeId = c.EmployeeId,
        EmployeeName = c.Employee == null ? null : c.Employee.FirstName + " " + c.Employee.LastName,
        EmployeePosition = c.Employee?.Position,
        IsActive = c.IsActive
    };

    [HttpGet]
    public async Task<ActionResult<List<FuelCardDto>>> List()
        => (await _db.FuelCards.Include(c => c.Employee).OrderBy(c => c.Label).ToListAsync())
            .Select(ToDto).ToList();

    [HttpPost]
    public async Task<ActionResult<FuelCardDto>> Create([FromBody] SaveFuelCardDto dto)
    {
        var card = new FuelCard();
        var err = await ApplyAsync(card, dto);
        if (err != null) return BadRequest(err);
        _db.FuelCards.Add(card);
        await _db.SaveChangesAsync();
        return ToDto(card);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<FuelCardDto>> Update(int id, [FromBody] SaveFuelCardDto dto)
    {
        var card = await _db.FuelCards.Include(c => c.Employee).FirstOrDefaultAsync(c => c.Id == id);
        if (card == null) return NotFound();
        var err = await ApplyAsync(card, dto);
        if (err != null) return BadRequest(err);
        card.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return ToDto(card);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var card = await _db.FuelCards.FindAsync(id);
        if (card == null) return NotFound();
        _db.FuelCards.Remove(card);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Shared create/update mapping; returns a Slovak error or null.</summary>
    private async Task<string?> ApplyAsync(FuelCard card, SaveFuelCardDto dto)
    {
        card.Label = dto.Label.Trim();
        card.Note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim();
        card.IsActive = dto.IsActive;

        if (dto.EmployeeId is > 0)
        {
            var emp = await _db.Employees.FindAsync(dto.EmployeeId.Value);
            if (emp == null) return "Zamestnanec neexistuje.";
            card.EmployeeId = emp.Id;
            card.Employee = emp;
        }
        else
        {
            card.EmployeeId = null;
            card.Employee = null;
        }
        return null;
    }
}
