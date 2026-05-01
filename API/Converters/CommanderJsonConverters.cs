using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace API.Converters;

// JsonConverters for the Commander v1 REST API.
//
// The Commander spec is explicit (verbatim from the PDF):
//
//   "Numeric values must be checked as they can be shown as string or with
//    comma as decimal point"
//   "Empty values of attributes can have values of: empty string (""), null,
//    or 0. When specified, 0 must be taken as not empty value."
//
// So a single field can come back as any of:
//   - JSON number     (49.208797)
//   - dot-decimal     ("49.208797")
//   - comma-decimal   ("49,208797")    — Slovak / EU formatting
//   - integer         (5000)
//   - integer-string  ("5000")
//   - null            (null)
//   - empty string    ("")
//
// These converters normalise all of those: empty / null becomes C# null;
// a real 0 stays 0; everything else parses to its numeric value. Any
// unparseable string raises JsonException, which the client catches and
// surfaces as InvalidResponse — never returns garbage to the frontend.

/// <summary>Nullable double that accepts JSON number, comma- or dot-decimal string, null, or empty string.</summary>
public sealed class FlexibleNullableDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.GetDouble();
            case JsonTokenType.String:
            {
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                var normalised = s.Replace(',', '.').Trim();
                if (double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return v;
                throw new JsonException($"Cannot parse '{s}' as double");
            }
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for nullable double");
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>Nullable long — same flexibility rules; integer target.</summary>
public sealed class FlexibleNullableLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var i)) return i;
                if (reader.TryGetDouble(out var d)) return (long)d;
                throw new JsonException("Number out of range for long");
            case JsonTokenType.String:
            {
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                var normalised = s.Replace(',', '.').Trim();
                if (long.TryParse(normalised, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    return v;
                if (double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                    return (long)dv;
                throw new JsonException($"Cannot parse '{s}' as long");
            }
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for nullable long");
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>Nullable int — same flexibility rules; 32-bit target.</summary>
public sealed class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var i)) return i;
                if (reader.TryGetDouble(out var d)) return (int)d;
                throw new JsonException("Number out of range for int");
            case JsonTokenType.String:
            {
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                var normalised = s.Replace(',', '.').Trim();
                if (int.TryParse(normalised, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    return v;
                if (double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                    return (int)dv;
                throw new JsonException($"Cannot parse '{s}' as int");
            }
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for nullable int");
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>
/// String that may arrive as a JSON number. The Commander spec is inconsistent:
/// vehicleId is a string in /vehicles ("3653622561558") but a JSON number in
/// /last-positions (123456). We canonicalise to string everywhere on our side
/// so the frontend gets one consistent type.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var l)) return l.ToString(CultureInfo.InvariantCulture);
                if (reader.TryGetDouble(out var d)) return d.ToString(CultureInfo.InvariantCulture);
                throw new JsonException("Number out of range for string conversion");
            case JsonTokenType.True:  return "true";
            case JsonTokenType.False: return "false";
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for string");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value != null) writer.WriteStringValue(value);
        else writer.WriteNullValue();
    }
}
