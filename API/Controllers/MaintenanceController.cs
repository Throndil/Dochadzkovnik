using API.Data;
using API.Services;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// One-off maintenance endpoints. Superadmin-only.
/// </summary>
[ApiController]
[Route("api/maintenance")]
[Authorize]
public class MaintenanceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Cloudinary _cloudinary;
    private readonly ILogger<MaintenanceController> _log;
    private readonly string _root;

    // Old top-level folders this migration is allowed to touch. Anything not
    // under these (e.g. assets from other projects in a shared Cloudinary
    // account) is left strictly alone.
    private static readonly string[] KnownFolders =
    {
        "work-photos", "employee-photos", "car-photos", "location-photos",
        "material-photos", "receipts", "work-diaries"
    };

    public MaintenanceController(AppDbContext db, Cloudinary cloudinary,
        IConfiguration config, ILogger<MaintenanceController> log)
    {
        _db = db;
        _cloudinary = cloudinary;
        _log = log;
        _root = (config["Cloudinary:ProjectFolder"] ?? "profistav").Trim().Trim('/');
    }

    /// <summary>
    /// POST /api/maintenance/migrate-photo-folders?apply=false
    ///
    /// Moves existing image assets under the project root (profistav/) and,
    /// for work-photos, rewrites the location segment from "{id}" to
    /// "{id}-{slug}". Updates the stored DB URLs to match. Idempotent (assets
    /// already under the root are skipped) and scoped to this project's known
    /// folders only. Raw assets (invoice PDFs) are intentionally left as-is.
    ///
    /// Defaults to a DRY RUN — pass ?apply=true to actually rename. ALWAYS run
    /// the dry run first and review the report. Test on the dev environment
    /// before production.
    /// </summary>
    [HttpPost("migrate-photo-folders")]
    public async Task<IActionResult> MigratePhotoFolders([FromQuery] bool apply = false)
    {
        if (!User.HasClaim("isSuperAdmin", "true")) return Forbid();

        var locationNames = await _db.Locations.ToDictionaryAsync(l => l.Id, l => l.Name);

        int scanned = 0, migrated = 0, skippedAlready = 0, skippedForeign = 0, failed = 0;
        var samples = new List<object>();

        // Rename one asset URL; returns the new URL, or null when nothing was
        // done (already migrated / foreign / not an image / failure).
        async Task<string?> MigrateUrlAsync(string url)
        {
            scanned++;
            if (!TryParseImagePublicId(url, out var publicId))
            {
                skippedForeign++; // raw asset or unparseable — leave it
                return null;
            }
            if (publicId.StartsWith($"{_root}/", StringComparison.Ordinal))
            {
                skippedAlready++;
                return null;
            }
            var topFolder = publicId.Split('/', 2)[0];
            if (Array.IndexOf(KnownFolders, topFolder) < 0)
            {
                skippedForeign++; // not ours — never touch other projects' assets
                return null;
            }

            var newPublicId = BuildNewPublicId(publicId, locationNames);
            if (samples.Count < 25)
                samples.Add(new { from = publicId, to = newPublicId });

            if (!apply)
            {
                migrated++; // would-migrate count in dry run
                return null;
            }

            try
            {
                var res = await _cloudinary.RenameAsync(new RenameParams(publicId, newPublicId) { Overwrite = false });
                if (res.Error != null || res.SecureUrl == null)
                {
                    failed++;
                    _log.LogWarning("[PhotoMigration] Rename failed {From} → {To}: {Err}",
                        publicId, newPublicId, res.Error?.Message);
                    return null;
                }
                migrated++;
                return res.SecureUrl.ToString();
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogError(ex, "[PhotoMigration] Rename threw for {From}", publicId);
                return null;
            }
        }

        // ── Single-URL fields ──
        async Task MigrateSingle<T>(IQueryable<T> rows, Func<T, string?> get, Action<T, string> set) where T : class
        {
            foreach (var row in await rows.ToListAsync())
            {
                var url = get(row);
                if (string.IsNullOrWhiteSpace(url)) continue;
                var newUrl = await MigrateUrlAsync(url);
                if (newUrl != null) set(row, newUrl);
            }
        }

        await MigrateSingle(_db.Employees.Where(e => e.PhotoUrl != null), e => e.PhotoUrl, (e, u) => e.PhotoUrl = u);
        await MigrateSingle(_db.Cars.Where(c => c.PhotoUrl != null), c => c.PhotoUrl, (c, u) => c.PhotoUrl = u);
        await MigrateSingle(_db.Locations.Where(l => l.PhotoUrl != null), l => l.PhotoUrl, (l, u) => l.PhotoUrl = u);
        await MigrateSingle(_db.WorkPhotos, w => w.PhotoUrl, (w, u) => w.PhotoUrl = u);
        await MigrateSingle(_db.MaterialUsages.Where(m => m.PhotoUrl != null), m => m.PhotoUrl, (m, u) => m.PhotoUrl = u);
        await MigrateSingle(_db.MaterialPurchases.Where(m => m.ReceiptPhotoUrl != null), m => m.ReceiptPhotoUrl, (m, u) => m.ReceiptPhotoUrl = u);
        await MigrateSingle(_db.WorkDiaries.Where(d => d.AttachmentUrl != null), d => d.AttachmentUrl, (d, u) => d.AttachmentUrl = u);

        // ── TimeEntry.PhotoUrl is a comma-separated list ──
        foreach (var entry in await _db.TimeEntries.Where(t => t.PhotoUrl != null && t.PhotoUrl != "").ToListAsync())
        {
            var urls = entry.PhotoUrl!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var changed = false;
            for (var i = 0; i < urls.Length; i++)
            {
                var newUrl = await MigrateUrlAsync(urls[i]);
                if (newUrl != null) { urls[i] = newUrl; changed = true; }
            }
            if (changed) entry.PhotoUrl = string.Join(",", urls);
        }

        if (apply) await _db.SaveChangesAsync();

        return Ok(new
        {
            mode = apply ? "APPLIED" : "DRY_RUN",
            projectRoot = _root,
            scanned,
            migrated,
            skippedAlready,
            skippedForeign,
            failed,
            sample = samples
        });
    }

    /// <summary>
    /// Extract the image public-id (folders + name, no extension) from a
    /// Cloudinary image URL. Returns false for raw assets or anything that
    /// doesn't look like an /image/upload/ URL.
    /// </summary>
    private static bool TryParseImagePublicId(string url, out string publicId)
    {
        publicId = "";
        try
        {
            var path = new Uri(url).AbsolutePath;             // /image/upload/v123/folder/id.ext
            const string marker = "/image/upload/";
            var idx = path.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return false;                         // raw or unexpected → skip
            var after = path[(idx + marker.Length)..];         // v123/folder/id.ext
            var slash = after.IndexOf('/');
            if (slash < 0) return false;
            var withExt = after[(slash + 1)..];                // folder/id.ext  (drop version)
            var ext = Path.GetExtension(withExt);
            publicId = string.IsNullOrEmpty(ext) ? withExt : withExt[..^ext.Length];
            return publicId.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prepend the project root and, for work-photos, rewrite the location
    /// segment "{id}" → "{id}-{slug}".
    /// </summary>
    private string BuildNewPublicId(string publicId, IReadOnlyDictionary<int, string> locationNames)
    {
        if (publicId.StartsWith("work-photos/", StringComparison.Ordinal))
        {
            var parts = publicId.Split('/');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var locId))
            {
                locationNames.TryGetValue(locId, out var name);
                parts[1] = CloudinaryFolders.LocationSegment(locId, name);
                publicId = string.Join('/', parts);
            }
        }
        return $"{_root}/{publicId}";
    }
}
