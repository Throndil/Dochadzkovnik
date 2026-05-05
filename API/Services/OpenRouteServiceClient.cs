using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace API.Services;

/// <summary>
/// OpenRouteService implementation of <see cref="IRouteSnappingService"/>.
/// Free-tier limits as of 2026: 2 000 directions requests / day, 40 / minute.
/// We cache by rounded start/stop coords so repeat lookups don't burn quota.
///
/// Security contract:
///   - API key is read from <c>OpenRouteService:ApiKey</c> (env
///     <c>OpenRouteService__ApiKey</c>). Never logged. Never serialised into
///     a DTO. Never echoed in an exception message.
///   - All errors are caught and turned into <c>null</c>. The frontend will
///     fall back to the existing dashed straight line, so an outage degrades
///     gracefully — the customer never sees an "ORS down" panel.
///
/// Caching:
///   - In-memory only (IMemoryCache). 7-day TTL. Survives the lifetime of
///     the API process; lost on Railway redeploy. For ~30 rides/day across
///     this customer's fleet that's fine — we'd re-fill the cache in a few
///     hours and stay well under the 2 000/day cap. If quota ever becomes
///     a concern, swap this for a CommanderRouteCache table (per the
///     migration-safety rules: dotnet ef migrations add ...).
/// </summary>
public sealed class OpenRouteServiceClient : IRouteSnappingService
{
    /// <summary>
    /// 7 days. Roads change on the order of months, so this is conservative
    /// and the cache mostly serves repeat trips by the same vehicle.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// Coordinate-precision used in the cache key. 5 decimal places ≈ 1.1 m.
    /// Two rides starting/ending within ~1 m of each other are the same drive
    /// for our purposes; this keeps the hit rate high without being silly.
    /// </summary>
    private const int CacheKeyDecimals = 5;

    /// <summary>
    /// Endpoint for driving-car directions in GeoJSON form. The <c>/geojson</c>
    /// suffix is what makes the response a FeatureCollection rather than the
    /// default encoded-polyline shape — easier to parse, easier to render in
    /// Leaflet without an extra polyline-decoder dependency.
    /// </summary>
    private const string DirectionsUrl =
        "https://api.openrouteservice.org/v2/directions/driving-car/geojson";

    /// <summary>
    /// Pelias-based reverse geocoder. Free-tier limits (as of 2026): 1 000
    /// requests/day, 100/min — separate from the directions quota. We cache
    /// at lower coord precision than directions (3 decimals ≈ 110 m), so a
    /// vehicle moving along a street still hits the cache for adjacent
    /// readings rather than burning a fresh call every 11 m.
    /// </summary>
    private const string ReverseGeocodeUrl =
        "https://api.openrouteservice.org/geocode/reverse";

    /// <summary>
    /// 3 decimal places ≈ 110 m. Tighter than that and a moving vehicle's
    /// per-poll positions would round into different cache cells, burning
    /// quota. Looser than that and "Bratislava, Petržalka" might be served
    /// from a key that no longer matches the current street.
    /// </summary>
    private const int ReverseGeocodeKeyDecimals = 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OpenRouteServiceClient> _logger;
    private readonly string _apiKey;

    public OpenRouteServiceClient(
        HttpClient http,
        IMemoryCache cache,
        IConfiguration config,
        ILogger<OpenRouteServiceClient> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
        // Accept any of these env-var spellings — operators have shipped each
        // variant at one point or another, and silently short-circuiting on a
        // misnamed key is exactly the trap SECRETS.md warns about. Stable
        // canonical name is OpenRouteService__ApiKey (→ OpenRouteService:ApiKey),
        // but we also read OpenRouteService__API and OpenRouteService__Key so
        // the customer's existing Railway config keeps working without a
        // rename. First non-empty match wins.
        _apiKey =
            FirstNonEmpty(
                config["OpenRouteService:ApiKey"],
                config["OpenRouteService:API"],
                config["OpenRouteService:Key"])
            ?? string.Empty;

        // Single startup log so the operator can confirm ORS is wired without
        // trial-and-error. Length only — never the value or even a prefix.
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogInformation(
                "OpenRouteService: API key not configured. Snap-route + reverse-geocode will short-circuit to fallback (dashed line / coords). Set OpenRouteService__ApiKey to enable.");
        }
        else
        {
            _logger.LogInformation(
                "OpenRouteService: API key configured ({Length} chars). Snap-route + reverse-geocode enabled.",
                _apiKey.Length);
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    public async Task<SnappedRoute?> SnapAsync(
        double startLat, double startLon,
        double stopLat,  double stopLon,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            // Don't log a warning each call — the operator may have deliberately
            // not configured ORS yet. Single info on the first call would be
            // ideal, but we keep things quiet to avoid log spam.
            return null;
        }

        var key = BuildCacheKey(startLat, startLon, stopLat, stopLon);
        if (_cache.TryGetValue<SnappedRoute>(key, out var cached) && cached != null)
            return cached;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, DirectionsUrl);
            // Per ORS spec the API key goes in the Authorization header.
            // Never on DefaultRequestHeaders; never logged.
            req.Headers.TryAddWithoutValidation("Authorization", _apiKey);
            req.Headers.Accept.ParseAdd("application/json, application/geo+json");

            // ORS expects [lon, lat] pairs.
            req.Content = JsonContent.Create(new
            {
                coordinates = new[]
                {
                    new[] { startLon, startLat },
                    new[] { stopLon,  stopLat  }
                }
            });

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Log the status so an operator can diagnose quota / outage,
                // but never the API key, the body (it can echo coords), or
                // the request URL with parameters. Status code is enough.
                _logger.LogWarning(
                    "ORS returned non-success: {StatusCode}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var route = ParseGeoJson(doc.RootElement);
            if (route != null)
                _cache.Set(key, route, CacheTtl);
            return route;
        }
        catch (OperationCanceledException)
        {
            throw; // honour cancellation as cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ORS call failed.");
            return null;
        }
    }

    private static string BuildCacheKey(double sLat, double sLon, double eLat, double eLon)
    {
        var sl = Math.Round(sLat, CacheKeyDecimals);
        var so = Math.Round(sLon, CacheKeyDecimals);
        var el = Math.Round(eLat, CacheKeyDecimals);
        var eo = Math.Round(eLon, CacheKeyDecimals);
        return $"ors:{sl},{so}->{el},{eo}";
    }

    public async Task<string?> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return null;

        var rLat = Math.Round(lat, ReverseGeocodeKeyDecimals);
        var rLon = Math.Round(lon, ReverseGeocodeKeyDecimals);
        var key = $"ors:reverse:{rLat},{rLon}";

        if (_cache.TryGetValue<string>(key, out var cached))
            return cached;

        try
        {
            // Build URL with the API key in the Authorization header (NOT in
            // the query string, per security non-negotiables in COMMANDER_PLAN.md).
            var url = $"{ReverseGeocodeUrl}?point.lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&point.lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&size=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", _apiKey);
            req.Headers.Accept.ParseAdd("application/json, application/geo+json");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ORS reverse-geocode returned non-success: {StatusCode}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var label = ParseReverseGeocodeLabel(doc.RootElement);

            // Cache hit OR miss — store either way so we don't hammer the
            // service on every retry. null gets stored as null and TryGetValue
            // returns true with cached==null on the next call.
            _cache.Set(key, label, CacheTtl);
            return label;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ORS reverse-geocode call failed.");
            return null;
        }
    }

    /// <summary>
    /// Pull the human-readable label from a Pelias FeatureCollection. Shape
    /// we care about:
    /// <code>
    /// { "features": [ { "properties": { "label": "Pražská 1, 81106 Bratislava, Slovakia" } } ] }
    /// </code>
    /// Returns null on any structural mismatch or empty result set.
    /// </summary>
    private static string? ParseReverseGeocodeLabel(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array ||
            features.GetArrayLength() == 0)
            return null;

        var first = features[0];
        if (!first.TryGetProperty("properties", out var props)) return null;
        if (!props.TryGetProperty("label", out var label) ||
            label.ValueKind != JsonValueKind.String)
            return null;

        var s = label.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// Parse the ORS GeoJSON response. Shape we care about:
    /// <code>
    /// {
    ///   "features": [
    ///     {
    ///       "geometry": { "coordinates": [[lon, lat], ...], "type": "LineString" },
    ///       "properties": { "summary": { "distance": meters, "duration": seconds } }
    ///     }
    ///   ]
    /// }
    /// </code>
    /// Returns null on any structural mismatch — better to fall back to the
    /// dashed line than render a garbage polyline.
    /// </summary>
    private static SnappedRoute? ParseGeoJson(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array ||
            features.GetArrayLength() == 0)
            return null;

        var feature = features[0];
        if (!feature.TryGetProperty("geometry", out var geometry)) return null;
        if (!geometry.TryGetProperty("coordinates", out var coords) ||
            coords.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<double[]>(coords.GetArrayLength());
        foreach (var pair in coords.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
            list.Add(new[] { pair[0].GetDouble(), pair[1].GetDouble() });
        }
        if (list.Count < 2) return null;

        double distance = 0, duration = 0;
        if (feature.TryGetProperty("properties", out var props) &&
            props.TryGetProperty("summary", out var summary))
        {
            if (summary.TryGetProperty("distance", out var d) && d.ValueKind == JsonValueKind.Number)
                distance = d.GetDouble();
            if (summary.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                duration = dur.GetDouble();
        }

        return new SnappedRoute(list.ToArray(), distance, duration);
    }
}
