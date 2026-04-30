using API.Data;
using API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController]
[Route("api/feature-flags")]
public class FeatureFlagsController : ControllerBase
{
    private readonly AppDbContext _db;

    public FeatureFlagsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the current feature-flag state. Anonymous because the kiosk (no JWT)
    /// needs to know which features to render. Output is a flat dictionary keyed by
    /// feature key, e.g. { "notifications": true }. Lower-cased keys to match the
    /// frontend's signal property names.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<Dictionary<string, bool>>> Get()
    {
        var flags = await _db.FeatureFlags.ToListAsync();
        var result = flags.ToDictionary(
            f => char.ToLowerInvariant(f.Key[0]) + f.Key[1..],
            f => f.Enabled);
        return Ok(result);
    }

    /// <summary>
    /// Toggle a flag. Superadmin only — checked via the isSuperAdmin JWT claim.
    /// Body shape: { "enabled": true | false }.
    /// 404s if the flag key is unknown so feature-flag enumeration is not exposed
    /// to non-superadmins (defence in depth — Authorize already blocks them).
    /// </summary>
    [Authorize]
    [HttpPut("{key}")]
    public async Task<IActionResult> Update(string key, [FromBody] UpdateFlagDto dto)
    {
        if (!User.HasClaim("isSuperAdmin", "true"))
            return Forbid();

        // Match by case-insensitive comparison so the frontend can send "notifications"
        // while storage uses the canonical "Notifications" form.
        var flag = await _db.FeatureFlags
            .FirstOrDefaultAsync(f => f.Key.ToLower() == key.ToLower());
        if (flag == null) return NotFound();

        flag.Enabled = dto.Enabled;
        flag.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    public class UpdateFlagDto
    {
        public bool Enabled { get; set; }
    }
}
