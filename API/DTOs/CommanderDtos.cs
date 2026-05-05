using System.Text.Json.Serialization;
using API.Converters;

namespace API.DTOs;

// =========================================================================
// Inbound: shapes parsed from the Commander API.
//
// These match the Commander v1 spec exactly. Internal use only — they are
// never returned to the Angular frontend, never logged, never stored.
//
// Every numeric field uses the Flexible* converters because the spec warns
// numerics can come back as JSON number, dot-decimal string, comma-decimal
// string, null, or empty string. See CommanderJsonConverters.cs.
// =========================================================================

internal sealed class CommanderVehiclesResponseRaw
{
    [JsonPropertyName("vehicles")]
    public List<CommanderVehicleRaw>? Vehicles { get; set; }
}

internal sealed class CommanderVehicleResponseRaw
{
    // Per the spec, GET /vehicles/{vehicleId} returns the same shape as one entry of
    // the /vehicles array. The Commander servers wrap it under "vehicle" in some
    // tenants and not in others, so the client tries both.
    [JsonPropertyName("vehicle")]
    public CommanderVehicleRaw? Vehicle { get; set; }
}

internal sealed class CommanderVehicleRaw
{
    [JsonPropertyName("vehicleId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? VehicleId { get; set; }

    [JsonPropertyName("vehicleName")]
    public string? VehicleName { get; set; }

    [JsonPropertyName("vehicleRegistrationPlate")]
    public string? VehicleRegistrationPlate { get; set; }

    [JsonPropertyName("vehicleDefaultDriver")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? VehicleDefaultDriver { get; set; }

    [JsonPropertyName("lastCommunication")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? LastCommunication { get; set; }

    [JsonPropertyName("vin")]
    public string? Vin { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("manufactureYear")]
    public string? ManufactureYear { get; set; }

    [JsonPropertyName("commissioningDate")]
    public string? CommissioningDate { get; set; }

    [JsonPropertyName("mainFuelType")]
    public string? MainFuelType { get; set; }

    [JsonPropertyName("objectType")]
    public string? ObjectType { get; set; }
}

internal sealed class CommanderLastPositionsResponseRaw
{
    [JsonPropertyName("positions")]
    public List<CommanderPositionRaw>? Positions { get; set; }

    [JsonPropertyName("totalCount")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? TotalCount { get; set; }
}

internal sealed class CommanderPositionRaw
{
    [JsonPropertyName("vehicleId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? VehicleId { get; set; }

    [JsonPropertyName("gpsTime")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? GpsTime { get; set; }

    [JsonPropertyName("gpsLat")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? GpsLat { get; set; }

    [JsonPropertyName("gpsLon")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? GpsLon { get; set; }

    [JsonPropertyName("gpsSpeed")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? GpsSpeed { get; set; }

    [JsonPropertyName("carIgnition")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? CarIgnition { get; set; }

    [JsonPropertyName("voltage")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? Voltage { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

internal sealed class CommanderCurrentTachoResponseRaw
{
    [JsonPropertyName("currentTacho")]
    public CommanderCurrentTachoRaw? CurrentTacho { get; set; }
}

internal sealed class CommanderCurrentTachoRaw
{
    [JsonPropertyName("km")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? Km { get; set; }

    [JsonPropertyName("engine_hours")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? EngineHours { get; set; }
}

internal sealed class CommanderErrorResponseRaw
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class CommanderRidesResponseRaw
{
    [JsonPropertyName("rides")]
    public List<CommanderRideRaw>? Rides { get; set; }

    [JsonPropertyName("page")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? Page { get; set; }

    [JsonPropertyName("totalPages")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? TotalPages { get; set; }

    [JsonPropertyName("totalCount")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? TotalCount { get; set; }
}

/// <summary>
/// Subset of the Commander ride object we actually need for aggregation.
/// The spec returns 30+ fields per ride — we read only the four that drive
/// bucket assignment + sums. Adding more fields is a future-compatible change.
/// </summary>
internal sealed class CommanderRideRaw
{
    [JsonPropertyName("rideId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? RideId { get; set; }

    /// <summary>Ride start, Unix seconds. Drives which day-bucket the ride falls into.</summary>
    [JsonPropertyName("startTime")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? StartTime { get; set; }

    [JsonPropertyName("stopTime")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? StopTime { get; set; }

    /// <summary>Distance in km. Spec example shows 350.23 for Bratislava-Košice.</summary>
    [JsonPropertyName("distance")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? Distance { get; set; }

    /// <summary>Duration in seconds.</summary>
    [JsonPropertyName("duration")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? Duration { get; set; }

    [JsonPropertyName("avgSpeed")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? AvgSpeed { get; set; }

    /// <summary>"BUSINESS_RIDE" or "PRIVAT_RIDE". Per the spec, GPS coords + addresses are returned ONLY for business rides.</summary>
    [JsonPropertyName("rideType")]
    public string? RideType { get; set; }

    [JsonPropertyName("driverName")]
    public string? DriverName { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("latStart")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? LatStart { get; set; }

    [JsonPropertyName("lonStart")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? LonStart { get; set; }

    [JsonPropertyName("latStop")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? LatStop { get; set; }

    [JsonPropertyName("lonStop")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? LonStop { get; set; }

    [JsonPropertyName("startAddress")]
    public string? StartAddress { get; set; }

    [JsonPropertyName("stopAddress")]
    public string? StopAddress { get; set; }
}

// =========================================================================
// Outbound: shapes returned from /api/commander/* to the Angular frontend.
//
// Sanitised by construction — no credentials, no echoed Commander error
// strings, no internal flags. Only the fields the UI needs.
// =========================================================================

public sealed class CommanderVehicleDto
{
    public string VehicleId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? RegistrationPlate { get; set; }
    public string? Vin { get; set; }
    public DateTime? LastCommunication { get; set; }
}

public sealed class CommanderVehicleDetailDto
{
    public string VehicleId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? RegistrationPlate { get; set; }
    public string? Vin { get; set; }
    public DateTime? LastCommunication { get; set; }
    public string? Model { get; set; }
    public string? ManufactureYear { get; set; }
    public string? CommissioningDate { get; set; }
    public string? MainFuelType { get; set; }
    public string? ObjectType { get; set; }
}

public sealed class CommanderPositionDto
{
    public string VehicleId { get; set; } = string.Empty;
    public DateTime? GpsTime { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? SpeedKmh { get; set; }
    public bool? IgnitionOn { get; set; }
    public double? VoltageVolts { get; set; }
    public string? Address { get; set; }
}

public sealed class CommanderTachoDto
{
    public double? Km { get; set; }
    public double? EngineHours { get; set; }
}

/// <summary>Aggregated ride totals for one time bucket (Dnes / Včera / etc).</summary>
public sealed class CommanderRideBucketDto
{
    public int RideCount { get; set; }
    public double DistanceKm { get; set; }
    public long DurationSeconds { get; set; }
    /// <summary>
    /// Average speed across the bucket = TotalDistanceKm / TotalDrivingHours.
    /// 0 when DurationSeconds is 0 (no rides at all). NOT a running mean of
    /// per-ride speeds: a 1 km / 30 min ride and a 100 km / 1 h ride sum to
    /// 101 km / 1.5 h ⇒ ~67 km/h, which is what the manager wants — the
    /// fleet's effective speed for the period, not a per-ride mean.
    /// </summary>
    public double AvgSpeedKmh { get; set; }
}

/// <summary>
/// Five buckets that mirror Commander's own "Prehľad jázd" panel:
/// Dnes / Včera / Aktuálny týždeň / Tento mesiac / Minulý mesiac.
/// Bucketing is by ride.startTime in Europe/Bratislava local time.
///
/// <see cref="Truncated"/> is true if the underlying paginated /rides call hit
/// our page-cap before reading every ride in the window — sums are then
/// lower bounds, not exact. See CommanderClient.GetRideSummaryAsync.
/// </summary>
public sealed class CommanderRideSummaryDto
{
    public CommanderRideBucketDto Today { get; set; } = new();
    public CommanderRideBucketDto Yesterday { get; set; } = new();
    public CommanderRideBucketDto CurrentWeek { get; set; } = new();
    public CommanderRideBucketDto ThisMonth { get; set; } = new();
    public CommanderRideBucketDto LastMonth { get; set; } = new();
    public bool Truncated { get; set; }
}

/// <summary>
/// One ride, sanitised for the frontend. Per the Commander spec, GPS coords
/// and addresses are returned ONLY for <c>BUSINESS_RIDE</c>; for
/// <c>PRIVAT_RIDE</c> these properties are null and the ride should be
/// rendered as a list entry without a map link.
/// </summary>
public sealed class CommanderRideDetailDto
{
    public string RideId { get; set; } = string.Empty;
    public DateTime? StartTime { get; set; }
    public DateTime? StopTime { get; set; }
    public long? DurationSeconds { get; set; }
    public double? DistanceKm { get; set; }
    public double? AvgSpeedKmh { get; set; }
    public string? RideType { get; set; }
    public string? DriverName { get; set; }
    public string? Note { get; set; }
    public double? LatStart { get; set; }
    public double? LonStart { get; set; }
    public double? LatStop { get; set; }
    public double? LonStop { get; set; }
    public string? StartAddress { get; set; }
    public string? StopAddress { get; set; }

    // ─── Local attribution ────────────────────────────────────────
    // Filled by CommanderController.GetRecentRides after pairing the
    // Commander vehicle with our local Car (by license plate) and
    // looking up the TimeEntry whose [ClockIn, ClockOut] window contained
    // this ride's StartTime. Null when no matching entry was found —
    // either the worker didn't book that car for that shift, or the ride
    // happened outside any clocked-in window (e.g. weekend personal use).
    public int? AttributedEmployeeId { get; set; }
    public string? AttributedEmployeeName { get; set; }
    public int? AttributedLocationId { get; set; }
    public string? AttributedLocationName { get; set; }
}

public sealed class CommanderErrorDto
{
    public string Error { get; set; } = string.Empty;     // Slovak, user-facing
    public string Code { get; set; } = string.Empty;      // e.g. "ratelimited", "network", "notfound"
    public bool Retryable { get; set; }
    public int? RetryAfterSeconds { get; set; }
}

/// <summary>
/// One snapped (road-following) ride path returned by the route-snapping
/// service. Coordinates use the GeoJSON [lon, lat] convention so the
/// frontend can feed them straight back into a Leaflet polyline (after
/// swapping to [lat, lon] — Leaflet's quirk, not GeoJSON's).
///
/// This is the routing engine's BEST GUESS, not the actual driven path.
/// The frontend labels it accordingly ("trasa po cestách (odhad)").
/// </summary>
public sealed class SnappedRouteDto
{
    public double[][] Coordinates { get; set; } = Array.Empty<double[]>();
    public double DistanceMeters { get; set; }
    public double DurationSeconds { get; set; }
}

/// <summary>
/// Per-vehicle stats batch returned by /api/commander/fleet-stats. Drives
/// the new Tachometer + Štatistiky columns on the Prehlad table: the page
/// asks once, fills every row from the response.
///
/// <see cref="TachoKm"/> is null when the underlying /current-tacho call
/// failed for that vehicle (transient errors are swallowed per-vehicle so
/// one bad reply doesn't tank the whole batch). The three buckets are
/// always present but may have all-zero counts when the vehicle hasn't
/// driven in the period.
/// </summary>
public sealed class FleetVehicleStatsDto
{
    public string VehicleId { get; set; } = string.Empty;
    public double? TachoKm { get; set; }
    public CommanderRideBucketDto Today { get; set; } = new();
    public CommanderRideBucketDto CurrentWeek { get; set; } = new();
    public CommanderRideBucketDto ThisMonth { get; set; } = new();
}
