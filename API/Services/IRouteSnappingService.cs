namespace API.Services;

/// <summary>
/// One snapped route. Coordinates are in GeoJSON [lon, lat] order.
/// </summary>
public sealed record SnappedRoute(
    double[][] Coordinates,
    double DistanceMeters,
    double DurationSeconds);

/// <summary>
/// External-geo gateway. Two unrelated lookups, both implemented today via
/// OpenRouteService and behind the same API key — kept in one interface so
/// the controller takes a single dependency.
///
/// 1. <see cref="SnapAsync"/> — straight-line A→B turned into a road-following
///    polyline. Used to upgrade the dashed line between a ride's start and
///    stop into a real-looking driven path. Result is best-guess, not ground
///    truth (Commander's v1 API doesn't expose per-second GPS samples).
/// 2. <see cref="ReverseGeocodeAsync"/> — single coord → human-readable
///    location label (e.g. "Pražská 1, 81106 Bratislava, Slovakia"). Used in
///    the Prehlad / Detail "Poloha" cell when Commander itself doesn't return
///    an address (the customer's account doesn't have address resolution
///    enabled).
///
/// Implementations must:
///   - Never throw across the boundary. Return <c>null</c> on any error
///     (network, quota, "no route found", parse failure). Caller falls back
///     to coords or the dashed line so the page keeps working.
///   - Cache aggressively per call type. Same start/stop pair returns the
///     same route; same rounded coords return the same address.
///   - Never log the API key, even at Debug.
/// </summary>
public interface IRouteSnappingService
{
    Task<SnappedRoute?> SnapAsync(
        double startLat, double startLon,
        double stopLat,  double stopLon,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a human-readable address label for the given lat/lon, or null
    /// when the geocoder returns no result, the API key isn't configured, or
    /// any error path was hit. Caller treats null as "fall back to coords".
    /// </summary>
    Task<string?> ReverseGeocodeAsync(
        double lat, double lon,
        CancellationToken ct = default);
}
