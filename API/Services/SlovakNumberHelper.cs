using System.Globalization;

namespace API.Services;

/// <summary>
/// Parses and formats numeric strings the way they appear on Slovak supplier
/// invoices and on Document AI responses. SK uses comma decimal (<c>40,39</c>)
/// and space thousands separator (<c>1 788,43</c>); Document AI typically
/// returns en-US (<c>40.39</c>) but may pass through the source format when
/// in "raw text" mode. Both paths must round-trip cleanly.
///
/// Finance-grade: any unparseable input returns null rather than guessing.
/// </summary>
public static class SlovakNumberHelper
{
    private static readonly CultureInfo Sk = CultureInfo.GetCultureInfo("sk-SK");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Tries to parse a decimal in either SK or en-US format. Strips currency
    /// symbols (€, EUR), unicode no-break spaces, narrow no-break spaces, and
    /// trailing percent signs. Returns null on failure — caller decides whether
    /// to fall back, warn, or block.
    /// </summary>
    public static decimal? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim();

        // Strip currency markers and unit hints commonly seen on invoices
        s = s.Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
             .Replace("€", "", StringComparison.Ordinal)
             .Replace("%", "", StringComparison.Ordinal)
             .Trim();

        // Normalize whitespace: NBSP (U+00A0), narrow NBSP (U+202F), thin (U+2009)
        // → regular space, then strip them entirely (SK invoices use space as
        // thousands separator on the printed page).
        s = s.Replace(' ', ' ')
             .Replace(' ', ' ')
             .Replace(' ', ' ')
             .Replace(" ", "");

        if (s.Length == 0) return null;

        // Heuristic for decimal mark: SK uses comma. If there's exactly one
        // comma and no dots, treat comma as decimal. If both, the one further
        // right is the decimal separator. If only dots, en-US format.
        bool hasComma = s.Contains(',');
        bool hasDot   = s.Contains('.');

        if (hasComma && hasDot)
        {
            // Mixed: assume the rightmost is the decimal, the other is a
            // thousands separator we stripped above (defence in depth).
            var lastComma = s.LastIndexOf(',');
            var lastDot   = s.LastIndexOf('.');
            if (lastComma > lastDot)
            {
                s = s.Replace(".", "").Replace(',', '.');
            }
            else
            {
                s = s.Replace(",", "");
            }
        }
        else if (hasComma)
        {
            s = s.Replace(',', '.');
        }
        // else: only dots or no separator — already en-US-like.

        if (decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                             Inv, out var v))
            return v;
        return null;
    }

    /// <summary>SK-formatted decimal with two fixed places (e.g. <c>1 788,43</c>).</summary>
    public static string FormatMoney(decimal v) => v.ToString("N2", Sk);
}
