using System.Text.Json;
using System.Text.Json.Serialization;

namespace API.Converters;

/// <summary>
/// DateTime serialiser for the API.
///
/// All timestamps (ClockIn, ClockOut, CreatedAt …) are stored in the database as
/// local Bratislava time (DateTimeKind.Unspecified via Npgsql legacy mode).
///
/// We intentionally do NOT append a 'Z' / UTC offset when writing, so that the
/// ISO-8601 string arriving in the browser has no timezone indicator.
/// Angular's DatePipe (and JavaScript's Date constructor) then treats such a
/// string as local time, which is exactly what we want — no offset is applied.
///
/// Adding a 'Z' suffix would cause the browser to interpret the value as UTC and
/// shift it by the local UTC offset (e.g. +1 h for CET), producing times that
/// appear one hour too late.
///
/// On the READ side we also strip any timezone info coming in from the client so
/// that the value is stored as-is (local time) without an accidental UTC shift.
/// </summary>
public class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.SpecifyKind(reader.GetDateTime(), DateTimeKind.Unspecified);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Unspecified));
}
