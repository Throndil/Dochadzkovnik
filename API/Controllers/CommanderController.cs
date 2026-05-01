using API.DTOs;
using API.Filters;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    private readonly ILogger<CommanderController> _logger;

    public CommanderController(ICommanderClient commander, ILogger<CommanderController> logger)
    {
        _commander = commander;
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
    /// buckets (Dnes / Včera / 7 dní / Tento mesiac / Minulý mesiac) computed
    /// from the underlying /rides/{id} endpoint. Bucketed by ride.startTime in
    /// Europe/Bratislava local time. Cached server-side for 60 seconds.
    /// </summary>
    [HttpGet("vehicles/{vehicleId}/ride-summary")]
    public async Task<ActionResult<CommanderRideSummaryDto>> GetRideSummary(string vehicleId, CancellationToken ct)
        => ToActionResult(await _commander.GetRideSummaryAsync(vehicleId, ct));

    /// <summary>
    /// GET /api/commander/vehicles/{vehicleId}/rides — recent rides for one
    /// vehicle (last 7 days, newest first). Matches the semantics of the
    /// ride-summary's Last7Days bucket so list length equals that count.
    /// Cached server-side for 60 seconds.
    /// </summary>
    [HttpGet("vehicles/{vehicleId}/rides")]
    public async Task<ActionResult<List<CommanderRideDetailDto>>> GetRecentRides(string vehicleId, CancellationToken ct)
        => ToActionResult(await _commander.GetRecentRidesAsync(vehicleId, ct));

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
