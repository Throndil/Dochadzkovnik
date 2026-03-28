using System.Text.Json;
using System.Text.Json.Serialization;

namespace API.Converters;

/// <summary>
/// Ensures all DateTime values are serialised as UTC (with 'Z' suffix) so that
/// Angular's date pipe converts them to the correct local time in the browser.
/// Npgsql's legacy timestamp mode returns DateTimeKind.Unspecified which
/// System.Text.Json would serialise without a timezone indicator.
/// </summary>
public class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.SpecifyKind(reader.GetDateTime(), DateTimeKind.Utc);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
