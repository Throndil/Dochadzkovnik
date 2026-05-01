using API.DTOs;

namespace API.Services;

/// <summary>
/// Thin, read-only client over the Commander v1 REST API.
///
/// All methods return <see cref="CommanderResult{T}"/>; the controller translates
/// the result into HTTP status codes for the Angular frontend.
///
/// Security contract (also see COMMANDER_PLAN.md and SECRETS.md):
///   - The customer's Commander username/password are read from IConfiguration
///     ("Commander:Username" / ":Password") at request time.
///   - They are NEVER logged at any level, NEVER returned in a DTO, NEVER stored
///     in the database.
///   - HTTPS is enforced by the HttpClient registration in Program.cs.
///   - 429 + Retry-After is honoured exactly.
/// </summary>
public interface ICommanderClient
{
    /// <summary>List of all vehicles visible to the customer's Commander account. Cached 24h.</summary>
    Task<CommanderResult<List<CommanderVehicleDto>>> GetVehiclesAsync(CancellationToken ct);

    /// <summary>Single vehicle detail. Not cached.</summary>
    Task<CommanderResult<CommanderVehicleDetailDto>> GetVehicleAsync(string vehicleId, CancellationToken ct);

    /// <summary>Current GPS position for every vehicle. Cached 30s.</summary>
    Task<CommanderResult<List<CommanderPositionDto>>> GetLastPositionsAsync(CancellationToken ct);

    /// <summary>Current odometer (km) and engine hours for one vehicle. Not cached.</summary>
    Task<CommanderResult<CommanderTachoDto>> GetCurrentTachoAsync(string vehicleId, CancellationToken ct);

    /// <summary>
    /// Aggregated ride totals for the standard five buckets (today / yesterday /
    /// last 7 days / this month / last month), bucketed by ride.startTime in
    /// Europe/Bratislava local time. Cached per-vehicle for 60 seconds.
    /// </summary>
    Task<CommanderResult<CommanderRideSummaryDto>> GetRideSummaryAsync(string vehicleId, CancellationToken ct);

    /// <summary>
    /// Recent rides for one vehicle, ordered newest-first. Default window is
    /// the last 7 days. Cached per-vehicle for 60 seconds.
    /// </summary>
    Task<CommanderResult<List<CommanderRideDetailDto>>> GetRecentRidesAsync(string vehicleId, CancellationToken ct);
}

/// <summary>
/// Result envelope. Either Success with Data, or a typed failure with a Slovak
/// user-facing message and (where applicable) a Retry-After hint. Controllers
/// MUST NOT pass through Commander's raw error message: it can echo internal
/// IDs or hint at credentials.
/// </summary>
public sealed class CommanderResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public CommanderErrorKind ErrorKind { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public string UserMessage { get; init; } = string.Empty;

    public static CommanderResult<T> Ok(T data) => new()
    {
        Success = true,
        Data = data
    };

    public static CommanderResult<T> Error(CommanderErrorKind kind, string userMessage, TimeSpan? retryAfter = null) => new()
    {
        Success = false,
        ErrorKind = kind,
        UserMessage = userMessage,
        RetryAfter = retryAfter
    };
}

public enum CommanderErrorKind
{
    None = 0,
    Config,           // missing / invalid Commander:Username|Password|BaseUrl
    Network,          // can't reach Commander
    RateLimited,      // 429 — honour Retry-After
    NotFound,         // 404
    BadRequest,       // 4xx other than 429/404/401/403
    ServerError,      // 5xx
    InvalidResponse   // 200 with body we can't parse
}
