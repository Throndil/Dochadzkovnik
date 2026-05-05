using API.Data;
using API.DTOs;
using API.Filters;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

/// <summary>
/// Read-only proxy over the customer's Commander v1 fleet API.
///
/// Surface (M1):
///   GET /api/commander/vehicles                                — full list, cached server-side 24h
///   GET /api/commander/vehicles/{vehicleId}                    — single vehicle detail, live
///   GET /api/commander/last-positions                          — current GPS for every vehicle, cached 30s
///   GET /api/commander/vehicles/{vehicleId}/current-tacho      — odometer + engine hours, live
///
/// Auth: every action requires a JWT (admin or superadmin). The whole controller
/// is gated by the <c>CommanderIntegration</c> feature flag — non-superadmins get
/// 404 when the flag is off, keeping the feature invisible to the customer until
/// they have approved it.
///
/// Failure mapping:
///   <see cref="CommanderErrorKind.RateLimited"/> → 429 with Retry-After header
///   <see cref="CommanderErrorKind.NotFound"/>    → 404
///   <see cref="CommanderErrorKind.BadRequest"/>  → 400
///   <see cref="CommanderErrorKind.Config"/>      → 503 (nothing the manager can do)
///   <see cref="CommanderErrorKind.Network"/>     → 503
///   <see cref="CommanderErrorKind.ServerError"/> → 503
///   <see cref="CommanderErrorKind.InvalidResponse"/> → 503
///
/// All non-2xx responses use the <see cref="CommanderErrorDto"/> shape so the
/// frontend can render one consistent "Commander momentálne nedostupný" panel.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequireFeatureOrSuperAdmin("CommanderIntegration")]
public class CommanderController : ControllerBase
{
    private readonly ICommanderClient _commander;
    private readonly IRouteSnappingService _routes;
    private readonly AppDbContext _db;
    private readonly ILogger<CommanderController> _logger;

    public CommanderController(
        ICommanderClient commander,
        IRouteSnappingService routes,
        AppDbContext db,
        ILogger<CommanderController> logger)
    {
        _commander = commander;
        _routes = routes;
        _db = db;
        _logger = logger;
    }

    [HttpGet("vehicles")]
    public async Task<ActionResult<List<CommanderVehicleDto>>> GetVehicles(CancellationToken ct)
        => ToActionResult(await _commander.GetVehiclesAsync(ct));

    [HttpGet("vehicles/{vehicleId}")]
    public async Task<ActionResult<CommanderVehicleDetailDto>> GetVehicle(string vehicleId, CancellationToken ct)
        => ToActionResult(await _commander.GetVehicleAsync(vehicleId, ct));

    [HttpGet("last-positions")]
    public async Task<ActionResult<List<CommanderPositionDto>>> GetLastPositions(CancellationToken ct)
        => ToActionResult(await _commander.GetLastPositionsAsync(ct));

    [HttpGet("vehicles/{vehicleId}/current-tacho")]
    public async Task<ActionResult<CommanderTachoDto>> GetCurrentTacho(string vehicleId, CancellationToken ct)
        => ToActionResult(await _commander.GetCurrentTachoAsync(vehicleId, ct));

    /// <summary>
    /// GET /api/commander/vehicles/{vehicleId}/ride-summary — five aggregate
    /// buckets (Dnes / Včera / Aktuálny týždeň / Tento mesiac / Minulý mesiac) computed
    /// from the underlying /rides/{id} endpoint. Bucketed by ride.startTime in
    /// Europe/Bratislava local time. Cached server-side for 60 seconds.
    /// </summary>
    [HttpGet("vehicles/{vehicleId}/ride-summary")]
    public async Task<ActionResult<CommanderRideSummaryDto>> GetRideSummary(string vehicleId, CancellationToken ct)
        => ToActionResult(await _commander.GetRideSummaryAsync(vehicleId, ct));

    /// <summary>
    /// GET /api/commander/vehicles/{vehicleId}/rides — recent rides for one
    /// vehicle (last 7 days, newest first). Matches the semantics of the
    /// ride-summary's CurrentWeek bucket so list length equals that count.
    /// Cached server-side for 60 seconds.
    ///
    /// Each ride is enriched with local attribution: which Employee was
    /// booked onto this car on which Location for the calendar day the
    /// ride happened. Pairing logic:
    ///   1. Find the local Car whose normalised license plate equals the
    ///      Commander vehicleRegistrationPlate (alphanumeric-only, case
    ///      insensitive). If no match → no attribution for any ride.
    ///   2. For each ride, find the TimeEntry whose CarId matches AND whose
    ///      ClockIn falls on the same calendar day (Bratislava local) as
    ///      the ride's StartTime. Whole-day rule, NOT clock-window: a
    ///      worker who logged 10 hours on this car for this day owns every
    ///      ride that happened on it that day, regardless of the kiosk's
    ///      back-calculated 17:00 ClockOut anchor.
    /// Null fields on the response = "no booking on this car for this
    /// calendar day", which the frontend renders as no employee/location
    /// pill on that row.
    /// </summary>
    [HttpGet("vehicles/{vehicleId}/rides")]
    public async Task<ActionResult<List<CommanderRideDetailDto>>> GetRecentRides(string vehicleId, CancellationToken ct)
    {
        var ridesResult = await _commander.GetRecentRidesAsync(vehicleId, ct);
        if (!ridesResult.Success || ridesResult.Data == null)
            return ToActionResult(ridesResult);

        // Clone before enriching: GetRecentRidesAsync returns the cached list
        // by reference, and mutating it would persist Attributed* fields into
        // the IMemoryCache. A subsequently deleted TimeEntry would then leave
        // stale attribution in the cache for up to RideSummaryCacheTtl.
        var enriched = ridesResult.Data.Select(CloneRide).ToList();
        await EnrichRidesWithLocalAttributionAsync(vehicleId, enriched, ct);
        return Ok(enriched);
    }

    private static CommanderRideDetailDto CloneRide(CommanderRideDetailDto r) => new()
    {
        RideId          = r.RideId,
        StartTime       = r.StartTime,
        StopTime        = r.StopTime,
        DurationSeconds = r.DurationSeconds,
        DistanceKm      = r.DistanceKm,
        AvgSpeedKmh     = r.AvgSpeedKmh,
        RideType        = r.RideType,
        DriverName      = r.DriverName,
        Note            = r.Note,
        LatStart        = r.LatStart,
        LonStart        = r.LonStart,
        LatStop         = r.LatStop,
        LonStop         = r.LonStop,
        StartAddress    = r.StartAddress,
        StopAddress     = r.StopAddress,
        // Attributed* deliberately NOT copied — they start null on each
        // request and get filled by EnrichRidesWithLocalAttributionAsync.
    };

    /// <summary>
    /// Mutates each ride in place, filling AttributedEmployeeId/Name and
    /// AttributedLocationId/Name where a TimeEntry overlap exists. No-op when
    /// no ride has a usable StartTime, or when the Commander vehicle has no
    /// matching local Car.
    /// </summary>
    private async Task EnrichRidesWithLocalAttributionAsync(
        string vehicleId,
        List<CommanderRideDetailDto> rides,
        CancellationToken ct)
    {
        if (rides.Count == 0) return;

        // 1. Resolve local Car by plate (same normalisation rule the frontend
        //    car-detail panel uses). Commander returns the registration plate
        //    on the vehicle detail; refetch is fine — backend caches it.
        var vehicleResult = await _commander.GetVehicleAsync(vehicleId, ct);
        var commanderPlate = vehicleResult.Success ? vehicleResult.Data?.RegistrationPlate : null;
        if (string.IsNullOrWhiteSpace(commanderPlate)) return;
        var normalisedCommanderPlate = NormalisePlate(commanderPlate);
        if (string.IsNullOrEmpty(normalisedCommanderPlate)) return;

        var localCarId = await _db.Cars
            .Where(c => c.LicensePlate != null)
            .Select(c => new { c.Id, c.LicensePlate })
            .ToListAsync(ct);
        var matchedCar = localCarId.FirstOrDefault(
            c => NormalisePlate(c.LicensePlate) == normalisedCommanderPlate);
        if (matchedCar == null) return;

        // 2. Pull the time entries booked onto this car on any of the
        //    calendar days covered by the ride list. Bound the query by
        //    the response's overall date span so we don't scan the whole
        //    TimeEntries table; the per-row matching below uses .Date.
        var rideDates = rides
            .Where(r => r.StartTime.HasValue)
            .Select(r => r.StartTime!.Value.Date)
            .Distinct()
            .ToList();
        if (rideDates.Count == 0) return;

        var fromDate          = rideDates.Min();
        var toDateExclusive   = rideDates.Max().AddDays(1);

        // ClockIn / ClockOut are stored in Bratislava local time per the
        // project-wide UtcDateTimeConverter convention; ride.StartTime is
        // also Bratislava local (post 2026-05 timezone fix), so direct
        // DateTime / .Date comparisons are correct.
        var carId = matchedCar.Id;
        var timeEntries = await _db.TimeEntries
            .Where(te => te.CarId == carId)
            .Where(te => te.ClockIn >= fromDate && te.ClockIn < toDateExclusive)
            .Include(te => te.Employee)
            .Include(te => te.Location)
            .ToListAsync(ct);
        if (timeEntries.Count == 0) return;

        // 3. Pair each ride with the time entry booked on the SAME calendar
        //    day. Whole-day rule, not clock-window: a worker who logged
        //    10 hours on this car for May 5 owns every ride on the car
        //    that day, including ones outside the back-calculated
        //    [ClockIn..17:00] window the kiosk produces for past dates.
        //    First match wins if multiple workers booked the same car the
        //    same day (rare; flag for refinement if it ever shows up in
        //    real usage).
        foreach (var ride in rides)
        {
            if (!ride.StartTime.HasValue) continue;
            var rideDate = ride.StartTime.Value.Date;
            var match = timeEntries.FirstOrDefault(te => te.ClockIn.Date == rideDate);
            if (match == null) continue;

            ride.AttributedEmployeeId = match.EmployeeId;
            ride.AttributedEmployeeName = $"{match.Employee.FirstName} {match.Employee.LastName}".Trim();
            ride.AttributedLocationId = match.LocationId;
            ride.AttributedLocationName = match.Location.Name;
        }
    }

    /// <summary>
    /// Strip everything but A-Z / 0-9, uppercase. Same rule as
    /// CommanderService.normalisePlate on the frontend so the join works
    /// regardless of punctuation/space variations between data sources.
    /// </summary>
    private static string NormalisePlate(string? plate)
    {
        if (string.IsNullOrEmpty(plate)) return string.Empty;
        var sb = new System.Text.StringBuilder(plate.Length);
        foreach (var c in plate)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                sb.Append(c);
            else if (c >= 'a' && c <= 'z')
                sb.Append((char)(c - 32));
        }
        return sb.ToString();
    }

    /// <summary>
    /// GET /api/commander/fleet-stats — per-vehicle tacho + Today/7-day/Month
    /// roll-ups. Drives the Tachometer + Štatistiky columns on the Prehlad
    /// table. Cached server-side for 60 s; safe to call once per page load.
    /// </summary>
    [HttpGet("fleet-stats")]
    public async Task<ActionResult<List<FleetVehicleStatsDto>>> GetFleetStats(CancellationToken ct)
        => ToActionResult(await _commander.GetFleetStatsAsync(ct));

    /// <summary>
    /// GET /api/commander/snap-route?startLat=…&amp;startLon=…&amp;stopLat=…&amp;stopLon=…
    ///
    /// Best-guess driven path between two GPS points, snapped to the road
    /// network by an external routing service (currently OpenRouteService).
    /// 200 with a <see cref="SnappedRouteDto"/> on success; 204 No Content
    /// when the service returns null (no API key configured, no route found,
    /// outage, parse error, etc). The frontend treats 204 as "fall back to
    /// the dashed straight line", so the map keeps working in every failure
    /// mode without showing an error panel.
    ///
    /// Cached server-side for 7 days per (startLat, startLon, stopLat, stopLon)
    /// rounded to 5 decimal places — repeated rides on the same route are
    /// served from memory.
    /// </summary>
    [HttpGet("snap-route")]
    public async Task<IActionResult> SnapRoute(
        [FromQuery] double startLat,
        [FromQuery] double startLon,
        [FromQuery] double stopLat,
        [FromQuery] double stopLon,
        CancellationToken ct)
    {
        // Sanity: lat in [-90, 90], lon in [-180, 180]. Saves a round-trip
        // for callers that pass garbage.
        if (!IsValidLatLon(startLat, startLon) || !IsValidLatLon(stopLat, stopLon))
            return BadRequest(new { error = "Neplatné súradnice." });

        var snapped = await _routes.SnapAsync(startLat, startLon, stopLat, stopLon, ct);
        if (snapped == null) return NoContent();

        return Ok(new SnappedRouteDto
        {
            Coordinates = snapped.Coordinates,
            DistanceMeters = snapped.DistanceMeters,
            DurationSeconds = snapped.DurationSeconds,
        });
    }

    /// <summary>
    /// GET /api/commander/reverse-geocode?lat=…&amp;lon=…
    ///
    /// Single-coordinate reverse lookup via OpenRouteService geocoding.
    /// 200 with <c>{ "label": "Pražská 1, 81106 Bratislava, Slovakia" }</c>
    /// on success; 204 No Content when the geocoder returns no result, the
    /// API key isn't configured, or any error path was hit. Frontend treats
    /// 204 as "fall back to coords".
    ///
    /// Cached server-side for 7 days at 3-decimal coord precision (~110 m),
    /// so a vehicle moving along the same street segment returns the same
    /// label without burning quota.
    /// </summary>
    [HttpGet("reverse-geocode")]
    public async Task<IActionResult> ReverseGeocode(
        [FromQuery] double lat,
        [FromQuery] double lon,
        CancellationToken ct)
    {
        if (!IsValidLatLon(lat, lon))
            return BadRequest(new { error = "Neplatné súradnice." });

        var label = await _routes.ReverseGeocodeAsync(lat, lon, ct);
        if (string.IsNullOrEmpty(label)) return NoContent();
        return Ok(new { label });
    }

    private static bool IsValidLatLon(double lat, double lon)
        => lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

    private ActionResult<T> ToActionResult<T>(CommanderResult<T> r) where T : class
    {
        if (r.Success && r.Data != null)
            return Ok(r.Data);

        var error = new CommanderErrorDto
        {
            Error = r.UserMessage,
            Code = r.ErrorKind.ToString().ToLowerInvariant(),
            Retryable = r.ErrorKind is CommanderErrorKind.RateLimited
                                    or CommanderErrorKind.Network
                                    or CommanderErrorKind.ServerError,
            RetryAfterSeconds = r.RetryAfter is { } ra ? (int?)Math.Ceiling(ra.TotalSeconds) : null
        };

        switch (r.ErrorKind)
        {
            case CommanderErrorKind.RateLimited:
                if (error.RetryAfterSeconds is int s)
                    Response.Headers["Retry-After"] = s.ToString();
                return StatusCode(StatusCodes.Status429TooManyRequests, error);

            case CommanderErrorKind.NotFound:
                return NotFound(error);

            case CommanderErrorKind.BadRequest:
                return BadRequest(error);

            case CommanderErrorKind.Config:
                _logger.LogWarning("Commander request rejected upstream: configuration / auth invalid");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, error);

            case CommanderErrorKind.Network:
            case CommanderErrorKind.ServerError:
            case CommanderErrorKind.InvalidResponse:
            default:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, error);
        }
    }
}
