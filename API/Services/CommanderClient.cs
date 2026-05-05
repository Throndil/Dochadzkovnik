using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using API.DTOs;
using Microsoft.Extensions.Caching.Memory;

namespace API.Services;

/// <summary>
/// Read-only typed HttpClient for the Commander v1 REST API.
///
/// Security contract:
///   - Username/Password read from IConfiguration per call. Never assigned to
///     <c>HttpClient.DefaultRequestHeaders</c>. Never passed to a logger.
///   - <c>username:password</c> is base64-encoded inside one local variable that
///     does not escape this method. The plaintext password is not held anywhere
///     outside the request scope.
///   - All exception paths funnel through <see cref="SendAsync{T}"/> which
///     produces a Slovak user-facing message; Commander's own error string is
///     never propagated to the frontend.
///
/// Caching contract:
///   - <c>/vehicles</c> is cached for 24h. The Commander spec explicitly warns:
///       "Service is intended to be used only to read or update vehicle list and
///        it should be called e.g. once a day. Accounts calling these service all
///        the time can have limited access for the services."
///   - <c>/last-positions</c> is cached for 30 seconds so a manager refreshing
///     a panel doesn't burn the 300-req/window quota.
///   - Per-vehicle <c>/vehicles/{id}</c> and <c>/current-tacho/{id}</c> are NOT
///     cached: managers want live data when they open a specific car.
///   - Only successful 200 responses are cached. 429 / 5xx never poison the cache.
///
/// Failure contract (per <see cref="CommanderErrorKind"/>):
///   - 429 surfaces RateLimited + the spec's Retry-After value.
///   - 401/403 surfaces Config (the customer's account is misconfigured; treat
///     as service unavailable to the manager, log a warning for the operator).
///   - 5xx, network exceptions, timeouts surface Network/ServerError + the
///     "skúste o chvíľu" message.
///   - Body-parse failures surface InvalidResponse.
/// </summary>
public sealed class CommanderClient : ICommanderClient
{
    private static readonly TimeSpan VehiclesCacheTtl     = TimeSpan.FromHours(24);
    // /last-positions: lowered from 30 s to 10 s so the live "follow the dot"
    // mode on the Commander page reflects movement quickly enough for the
    // customer. At 10 s one admin in live mode burns 6 req/min on
    // /last-positions; even with multiple admins watching simultaneously we
    // stay well under Commander's 300-req/window cap.
    private static readonly TimeSpan PositionsCacheTtl    = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RideSummaryCacheTtl  = TimeSpan.FromSeconds(60);

    /// <summary>Hard cap on the number of /rides pages we'll follow per
    /// summary call. 100 rides/page × 5 = 500 rides covering ~31 days. If a
    /// vehicle exceeds this we mark the summary Truncated rather than burning
    /// rate-limit budget unbounded.</summary>
    private const int MaxRidePages = 5;

    private const string CacheKeyVehicles  = "commander:vehicles";
    private const string CacheKeyPositions = "commander:last-positions";

    private static string CacheKeyRideSummary(string vehicleId)
        => $"commander:ride-summary:{vehicleId}";

    private static string CacheKeyRecentRides(string vehicleId)
        => $"commander:recent-rides:{vehicleId}";

    private static readonly TimeZoneInfo BratislavaTz = ResolveBratislavaTz();

    private static TimeZoneInfo ResolveBratislavaTz()
    {
        // .NET 6+ supports IANA names cross-platform via ICU; on legacy Windows
        // setups fall back to the Windows registry name.
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava"); }
        catch (TimeZoneNotFoundException) { }
        try { return TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time"); }
        catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.Utc;
    }

    // Slovak user-facing strings. These are what the frontend ultimately shows
    // through the manager's panel, so they need to read like a normal Slovak
    // sentence, not marketing or jargon.
    private const string MsgUnavailable = "Commander momentálne nedostupný — pokus o obnovenie.";
    private const string MsgRateLimited = "Commander momentálne nedostupný — príliš veľa požiadaviek. Skúste o chvíľu znova.";
    private const string MsgNotFound    = "Commander záznam nenájdený.";
    private const string MsgBadRequest  = "Commander odmietol požiadavku.";
    private const string MsgInvalidResp = "Commander vrátil neočakávanú odpoveď.";
    private const string MsgConfig      = "Integrácia Commander nie je nastavená. Kontaktujte správcu.";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CommanderClient> _logger;

    public CommanderClient(HttpClient http, IConfiguration config, IMemoryCache cache, ILogger<CommanderClient> logger)
    {
        _http = http;
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    public async Task<CommanderResult<List<CommanderVehicleDto>>> GetVehiclesAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue<List<CommanderVehicleDto>>(CacheKeyVehicles, out var cached) && cached != null)
            return CommanderResult<List<CommanderVehicleDto>>.Ok(cached);

        var raw = await SendAsync<CommanderVehiclesResponseRaw>("vehicles", ct);
        if (!raw.Success)
            return CommanderResult<List<CommanderVehicleDto>>.Error(raw.ErrorKind, raw.UserMessage, raw.RetryAfter);

        var list = (raw.Data?.Vehicles ?? new List<CommanderVehicleRaw>())
            .Where(v => !string.IsNullOrWhiteSpace(v.VehicleId))
            .Select(MapToVehicleDto)
            .ToList();

        _cache.Set(CacheKeyVehicles, list, VehiclesCacheTtl);
        return CommanderResult<List<CommanderVehicleDto>>.Ok(list);
    }

    public async Task<CommanderResult<CommanderVehicleDetailDto>> GetVehicleAsync(string vehicleId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
            return CommanderResult<CommanderVehicleDetailDto>.Error(CommanderErrorKind.BadRequest, MsgBadRequest);

        var encoded = Uri.EscapeDataString(vehicleId);
        var raw = await SendAsync<CommanderVehicleResponseRaw>($"vehicles/{encoded}", ct);
        if (!raw.Success)
            return CommanderResult<CommanderVehicleDetailDto>.Error(raw.ErrorKind, raw.UserMessage, raw.RetryAfter);

        // Some Commander tenants wrap a single vehicle under "vehicle"; others
        // return the bare object. Try the wrapped form first; if Vehicle is null
        // and the body actually was the bare vehicle, fetch via the list endpoint
        // would be the fallback — but we only see that if the spec example here
        // doesn't match the tenant. For now: missing => NotFound.
        var v = raw.Data?.Vehicle;
        if (v == null || string.IsNullOrWhiteSpace(v.VehicleId))
            return CommanderResult<CommanderVehicleDetailDto>.Error(CommanderErrorKind.NotFound, MsgNotFound);

        return CommanderResult<CommanderVehicleDetailDto>.Ok(MapToVehicleDetailDto(v));
    }

    public async Task<CommanderResult<List<CommanderPositionDto>>> GetLastPositionsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue<List<CommanderPositionDto>>(CacheKeyPositions, out var cached) && cached != null)
            return CommanderResult<List<CommanderPositionDto>>.Ok(cached);

        var raw = await SendAsync<CommanderLastPositionsResponseRaw>("last-positions", ct);
        if (!raw.Success)
            return CommanderResult<List<CommanderPositionDto>>.Error(raw.ErrorKind, raw.UserMessage, raw.RetryAfter);

        var list = (raw.Data?.Positions ?? new List<CommanderPositionRaw>())
            .Where(p => !string.IsNullOrWhiteSpace(p.VehicleId))
            .Select(MapToPositionDto)
            .ToList();

        _cache.Set(CacheKeyPositions, list, PositionsCacheTtl);
        return CommanderResult<List<CommanderPositionDto>>.Ok(list);
    }

    public async Task<CommanderResult<CommanderTachoDto>> GetCurrentTachoAsync(string vehicleId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
            return CommanderResult<CommanderTachoDto>.Error(CommanderErrorKind.BadRequest, MsgBadRequest);

        var encoded = Uri.EscapeDataString(vehicleId);
        var raw = await SendAsync<CommanderCurrentTachoResponseRaw>($"current-tacho/{encoded}", ct);
        if (!raw.Success)
            return CommanderResult<CommanderTachoDto>.Error(raw.ErrorKind, raw.UserMessage, raw.RetryAfter);

        var t = raw.Data?.CurrentTacho;
        if (t == null)
            return CommanderResult<CommanderTachoDto>.Error(CommanderErrorKind.InvalidResponse, MsgInvalidResp);

        return CommanderResult<CommanderTachoDto>.Ok(new CommanderTachoDto
        {
            Km = t.Km,
            EngineHours = t.EngineHours
        });
    }

    public async Task<CommanderResult<CommanderRideSummaryDto>> GetRideSummaryAsync(string vehicleId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
            return CommanderResult<CommanderRideSummaryDto>.Error(CommanderErrorKind.BadRequest, MsgBadRequest);

        var cacheKey = CacheKeyRideSummary(vehicleId);
        if (_cache.TryGetValue<CommanderRideSummaryDto>(cacheKey, out var cached) && cached != null)
            return CommanderResult<CommanderRideSummaryDto>.Ok(cached);

        // Build the wide window in Bratislava local time, covering every
        // bucket the summary needs: 1st of last month 00:00 → now.
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, BratislavaTz);
        var firstOfThisMonthLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, nowLocal.Offset);
        var firstOfLastMonthLocal = firstOfThisMonthLocal.AddMonths(-1);

        var fromUnix = firstOfLastMonthLocal.ToUnixTimeSeconds();
        var toUnix   = nowLocal.ToUnixTimeSeconds();

        var encoded = Uri.EscapeDataString(vehicleId);
        var rides = new List<CommanderRideRaw>(capacity: 200);
        var truncated = false;

        for (var page = 1; page <= MaxRidePages; page++)
        {
            var url = $"rides/{encoded}?datetimeStart={fromUnix}&datetimeEnd={toUnix}&page={page}";
            var raw = await SendAsync<CommanderRidesResponseRaw>(url, ct);
            if (!raw.Success)
                return CommanderResult<CommanderRideSummaryDto>.Error(raw.ErrorKind, raw.UserMessage, raw.RetryAfter);

            var pageRides = raw.Data?.Rides ?? new List<CommanderRideRaw>();
            rides.AddRange(pageRides);

            var total = raw.Data?.TotalPages ?? 1;
            if (page >= total) break;
            if (page == MaxRidePages && total > MaxRidePages)
            {
                truncated = true;
                _logger.LogWarning(
                    "Commander ride summary truncated for {endpoint}: {totalPages} pages, capped at {maxPages}.",
                    url, total, MaxRidePages);
                break;
            }
        }

        var summary = AggregateRides(rides, nowLocal);
        summary.Truncated = truncated;

        _cache.Set(cacheKey, summary, RideSummaryCacheTtl);
        return CommanderResult<CommanderRideSummaryDto>.Ok(summary);
    }

    /// <summary>
    /// Sorts each ride into 0..N of the five buckets based on its startTime
    /// expressed in Europe/Bratislava local time. Rides that span midnight are
    /// counted in the bucket where they STARTED (matches Commander's own UX).
    /// Rides with a missing startTime, distance, or duration contribute zero.
    /// </summary>
    private static CommanderRideSummaryDto AggregateRides(IReadOnlyList<CommanderRideRaw> rides, DateTimeOffset nowLocal)
    {
        var todayStart        = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        var yesterdayStart    = todayStart.AddDays(-1);
        var sevenDaysStart    = todayStart.AddDays(-6); // includes today → 7 days
        var thisMonthStart    = new DateTimeOffset(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, nowLocal.Offset);
        var lastMonthStart    = thisMonthStart.AddMonths(-1);
        var lastMonthEnd      = thisMonthStart; // exclusive
        var tomorrowStart     = todayStart.AddDays(1);

        var summary = new CommanderRideSummaryDto();

        foreach (var r in rides)
        {
            if (!r.StartTime.HasValue) continue;
            var startLocal = TimeZoneInfo.ConvertTime(
                DateTimeOffset.FromUnixTimeSeconds(r.StartTime.Value), BratislavaTz);

            var distance = r.Distance ?? 0;
            var duration = r.Duration ?? 0;

            if (startLocal >= todayStart && startLocal < tomorrowStart)
                Add(summary.Today, distance, duration);

            if (startLocal >= yesterdayStart && startLocal < todayStart)
                Add(summary.Yesterday, distance, duration);

            if (startLocal >= sevenDaysStart && startLocal < tomorrowStart)
                Add(summary.Last7Days, distance, duration);

            if (startLocal >= thisMonthStart && startLocal < tomorrowStart)
                Add(summary.ThisMonth, distance, duration);

            if (startLocal >= lastMonthStart && startLocal < lastMonthEnd)
                Add(summary.LastMonth, distance, duration);
        }

        return summary;
    }

    private static void Add(CommanderRideBucketDto bucket, double km, long durationSec)
    {
        bucket.RideCount++;
        bucket.DistanceKm += km;
        bucket.DurationSeconds += durationSec;
    }

    public async Task<CommanderResult<List<CommanderRideDetailDto>>> GetRecentRidesAsync(string vehicleId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
            return CommanderResult<List<CommanderRideDetailDto>>.Error(CommanderErrorKind.BadRequest, MsgBadRequest);

        var cacheKey = CacheKeyRecentRides(vehicleId);
        if (_cache.TryGetValue<List<CommanderRideDetailDto>>(cacheKey, out var cached) && cached != null)
            return CommanderResult<List<CommanderRideDetailDto>>.Ok(cached);

        // Last 7 days in Bratislava local time. Re-using the same window
        // semantics as the summary's "Last7Days" bucket so the count in the
        // summary card matches the list length below it.
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, BratislavaTz);
        var sevenStartLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset)
            .AddDays(-6);

        var fromUnix = sevenStartLocal.ToUnixTimeSeconds();
        var toUnix = nowLocal.ToUnixTimeSeconds();
        var encoded = Uri.EscapeDataString(vehicleId);

        var rides = new List<CommanderRideRaw>(capacity: 100);

        for (var page = 1; page <= MaxRidePages; page++)
        {
            var url = $"rides/{encoded}?datetimeStart={fromUnix}&datetimeEnd={toUnix}&page={page}";
            var raw = await SendAsync<CommanderRidesResponseRaw>(url, ct);
            if (!raw.Success)
                return CommanderResult<List<CommanderRideDetailDto>>.Error(raw.ErrorKind, raw.UserMessage, raw.RetryAfter);

            var pageRides = raw.Data?.Rides ?? new List<CommanderRideRaw>();
            rides.AddRange(pageRides);

            var total = raw.Data?.TotalPages ?? 1;
            if (page >= total) break;
            if (page == MaxRidePages && total > MaxRidePages)
            {
                _logger.LogWarning(
                    "Commander recent rides truncated for {endpoint}: {totalPages} pages, capped at {maxPages}.",
                    url, total, MaxRidePages);
                break;
            }
        }

        var result = rides
            .Where(r => !string.IsNullOrEmpty(r.RideId))
            .OrderByDescending(r => r.StartTime ?? 0)
            .Select(MapToRideDetailDto)
            .ToList();

        _cache.Set(cacheKey, result, RideSummaryCacheTtl);
        return CommanderResult<List<CommanderRideDetailDto>>.Ok(result);
    }

    private static CommanderRideDetailDto MapToRideDetailDto(CommanderRideRaw r) => new()
    {
        RideId          = r.RideId ?? string.Empty,
        StartTimeUtc    = UnixSecondsToUtc(r.StartTime),
        StopTimeUtc     = UnixSecondsToUtc(r.StopTime),
        DurationSeconds = r.Duration,
        DistanceKm      = r.Distance,
        AvgSpeedKmh     = r.AvgSpeed,
        RideType        = NullIfEmpty(r.RideType),
        DriverName      = NullIfEmpty(r.DriverName),
        Note            = NullIfEmpty(r.Note),
        // Per spec: GPS + addresses are populated only for BUSINESS_RIDE.
        // For PRIVAT_RIDE the API returns nulls; we just pass them through.
        LatStart        = r.LatStart,
        LonStart        = r.LonStart,
        LatStop         = r.LatStop,
        LonStop         = r.LonStop,
        StartAddress    = NullIfEmpty(r.StartAddress),
        StopAddress     = NullIfEmpty(r.StopAddress),
    };

    // -----------------------------------------------------------------
    // core
    // -----------------------------------------------------------------

    private async Task<CommanderResult<T>> SendAsync<T>(string relativeUrl, CancellationToken ct) where T : class
    {
        var username = _config["Commander:Username"]?.Trim();
        var password = _config["Commander:Password"];

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            // No password value or username in the log — only the fact that
            // configuration is missing.
            _logger.LogWarning("Commander request blocked: credentials not configured (set Commander__Username / Commander__Password)");
            return CommanderResult<T>.Error(CommanderErrorKind.Config, MsgConfig);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);

            // Build the Basic-auth header here, scoped to this single request.
            // The base64 token is created from local variables only — it never
            // touches DefaultRequestHeaders, never enters a log call, never
            // gets serialised into a DTO.
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, ct);

            // 429 — honour Retry-After exactly. Spec: rate limit is 300/window per
            // company; X-RateLimit-* headers are present on success too but we
            // only act on 429.
            if (response.StatusCode == (HttpStatusCode)429)
            {
                var retry = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date is { } ra
                                ? ra - DateTimeOffset.UtcNow
                                : (TimeSpan?)null);
                _logger.LogWarning(
                    "Commander rate limited (429). Endpoint: {endpoint}. Retry-After: {seconds}s.",
                    relativeUrl, retry?.TotalSeconds ?? 0);

                return CommanderResult<T>.Error(
                    CommanderErrorKind.RateLimited,
                    MsgRateLimited,
                    retry is { TotalSeconds: > 0 } ? retry : TimeSpan.FromSeconds(60));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return CommanderResult<T>.Error(CommanderErrorKind.NotFound, MsgNotFound);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "Commander auth rejected ({status}). Endpoint: {endpoint}. Verify Commander__Username / Commander__Password on this environment.",
                    (int)response.StatusCode, relativeUrl);
                return CommanderResult<T>.Error(CommanderErrorKind.Config, MsgConfig);
            }

            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning(
                    "Commander server error ({status}). Endpoint: {endpoint}.",
                    (int)response.StatusCode, relativeUrl);
                return CommanderResult<T>.Error(CommanderErrorKind.ServerError, MsgUnavailable);
            }

            if ((int)response.StatusCode >= 400)
            {
                _logger.LogWarning(
                    "Commander client error ({status}). Endpoint: {endpoint}.",
                    (int)response.StatusCode, relativeUrl);
                return CommanderResult<T>.Error(CommanderErrorKind.BadRequest, MsgBadRequest);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            try
            {
                var parsed = JsonSerializer.Deserialize<T>(body, JsonOpts);
                if (parsed == null)
                    return CommanderResult<T>.Error(CommanderErrorKind.InvalidResponse, MsgInvalidResp);
                return CommanderResult<T>.Ok(parsed);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Commander returned unparseable JSON. Endpoint: {endpoint}.", relativeUrl);
                return CommanderResult<T>.Error(CommanderErrorKind.InvalidResponse, MsgInvalidResp);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate so the controller sees it cleanly.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient.Timeout elapsed; ct was not cancelled. Treat as Network.
            _logger.LogWarning(ex, "Commander request timed out. Endpoint: {endpoint}.", relativeUrl);
            return CommanderResult<T>.Error(CommanderErrorKind.Network, MsgUnavailable);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Commander request failed. Endpoint: {endpoint}.", relativeUrl);
            return CommanderResult<T>.Error(CommanderErrorKind.Network, MsgUnavailable);
        }
    }

    // -----------------------------------------------------------------
    // mapping (raw → outbound DTO)
    // -----------------------------------------------------------------

    private static CommanderVehicleDto MapToVehicleDto(CommanderVehicleRaw v) => new()
    {
        VehicleId            = v.VehicleId ?? string.Empty,
        Name                 = NullIfEmpty(v.VehicleName),
        RegistrationPlate    = NullIfEmpty(v.VehicleRegistrationPlate),
        Vin                  = NullIfEmpty(v.Vin),
        LastCommunicationUtc = UnixSecondsToUtc(v.LastCommunication)
    };

    private static CommanderVehicleDetailDto MapToVehicleDetailDto(CommanderVehicleRaw v) => new()
    {
        VehicleId            = v.VehicleId ?? string.Empty,
        Name                 = NullIfEmpty(v.VehicleName),
        RegistrationPlate    = NullIfEmpty(v.VehicleRegistrationPlate),
        Vin                  = NullIfEmpty(v.Vin),
        LastCommunicationUtc = UnixSecondsToUtc(v.LastCommunication),
        Model                = NullIfEmpty(v.Model),
        ManufactureYear      = NullIfEmpty(v.ManufactureYear),
        CommissioningDate    = NullIfEmpty(v.CommissioningDate),
        MainFuelType         = NullIfEmpty(v.MainFuelType),
        ObjectType           = NullIfEmpty(v.ObjectType)
    };

    private static CommanderPositionDto MapToPositionDto(CommanderPositionRaw p) => new()
    {
        VehicleId    = p.VehicleId ?? string.Empty,
        GpsTimeUtc   = UnixSecondsToUtc(p.GpsTime),
        Latitude     = p.GpsLat,
        Longitude    = p.GpsLon,
        SpeedKmh     = p.GpsSpeed,
        IgnitionOn   = p.CarIgnition.HasValue ? p.CarIgnition.Value != 0 : null,
        VoltageVolts = p.Voltage,
        Address      = NullIfEmpty(p.Address)
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Converts a Commander Unix-seconds timestamp to UTC DateTime.
    /// Returns null for missing / zero / out-of-range values; the spec says 0 must
    /// be treated as a real value for ordinary numerics, but for timestamps a 0
    /// reliably means "never communicated" rather than 1970-01-01.
    /// </summary>
    private static DateTime? UnixSecondsToUtc(long? unix)
    {
        if (!unix.HasValue || unix.Value <= 0) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix.Value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
