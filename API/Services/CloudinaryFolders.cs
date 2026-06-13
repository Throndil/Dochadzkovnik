using System.Globalization;
using System.Text;

namespace API.Services;

/// <summary>
/// Builds the (project-root-relative) Cloudinary folder paths for uploads.
/// The project root (e.g. "profistav/") is prepended centrally in
/// <see cref="CloudinaryStorageService"/>, so paths here stay relative.
///
/// Work-site photos live under a human-readable, stable folder:
/// <c>work-photos/{locationId}-{slug}/{yyyy-MM}</c>. The numeric id stays as a
/// prefix so renaming a Pracovisko never splits its photos across folders.
/// </summary>
public static class CloudinaryFolders
{
    public const string WorkPhotosRoot = "work-photos";

    /// <summary>work-photos/{id}-{slug}/{yyyy-MM}</summary>
    public static string WorkPhotos(int locationId, string? locationName, DateTime date)
        => $"{WorkPhotosRoot}/{LocationSegment(locationId, locationName)}/{date:yyyy-MM}";

    /// <summary>The "{id}-{slug}" folder segment for a location.</summary>
    public static string LocationSegment(int locationId, string? locationName)
    {
        var slug = Slug(locationName);
        return string.IsNullOrEmpty(slug) ? locationId.ToString() : $"{locationId}-{slug}";
    }

    /// <summary>
    /// Diacritics-stripped, lowercase, filesystem-safe slug.
    /// "Bratislava – Hlavná 5" → "bratislava-hlavna-5".
    /// </summary>
    public static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Strip diacritics (č → c, á → a, ...).
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch);
        }

        var ascii = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        // Any run of non-alphanumeric → single '-'; trim leading/trailing '-'.
        var outSb = new StringBuilder(ascii.Length);
        var lastDash = false;
        foreach (var ch in ascii)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                outSb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                outSb.Append('-');
                lastDash = true;
            }
        }

        return outSb.ToString().Trim('-');
    }
}
