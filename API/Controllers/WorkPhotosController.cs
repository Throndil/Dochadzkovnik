using API.Data;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/work-photos")]
[Authorize]
public class WorkPhotosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blob;

    public WorkPhotosController(AppDbContext db, IBlobStorageService? blob = null)
    {
        _db = db;
        _blob = blob;
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        var photo = await _db.WorkPhotos.FindAsync(id);
        if (photo == null) return NotFound();

        if (_blob != null && !string.IsNullOrEmpty(photo.PhotoUrl))
            await _blob.DeleteAsync(photo.PhotoUrl, "work-photos");

        _db.WorkPhotos.Remove(photo);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
