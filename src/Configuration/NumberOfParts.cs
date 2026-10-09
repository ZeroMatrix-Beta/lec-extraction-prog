using System;
using System.ComponentModel;
using System.Globalization;
using Newtonsoft.Json;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Represents the segment partitioning policy for lecture extraction.
/// Can be either a fixed positive integer (e.g. 2, 3) or "auto", which dynamically determines
/// the number of parts based on the video duration: one part per ~22.5 minutes, so a 45-minute
/// lecture gets 2 parts and a 90-minute double lecture gets 4.
/// [Human] Bestimmt, in wie viele Teile ein Video geschnitten wird: entweder eine feste Zahl (z.B. 2) oder "auto",
/// wodurch die Teile anhand der Videolänge dynamisch berechnet werden.
/// </summary>
[TypeConverter(typeof(NumberOfPartsTypeConverter))]
[JsonConverter(typeof(NumberOfPartsNewtonsoftJsonConverter))]
[System.Text.Json.Serialization.JsonConverter(typeof(NumberOfPartsSystemTextJsonConverter))]
public readonly struct NumberOfParts : IEquatable<NumberOfParts>, IEquatable<int>, IComparable<NumberOfParts>, IComparable<int> {
    private readonly int _value; // 0 = Auto, > 0 = Fixed part count

    public const string AutoLiteral = "auto";
    public static NumberOfParts Auto => new(0);

    public NumberOfParts(int fixedParts) {
        if (fixedParts < 0) {
            throw new ArgumentOutOfRangeException(nameof(fixedParts), "NumberOfParts must be >= 1 or 'auto'.");
        }
        _value = fixedParts;
    }

    public bool IsAuto => _value == 0;
    public int? FixedParts => _value > 0 ? _value : null;

    /// <summary>
    /// Resolves the effective number of parts for a given video duration in seconds.
    /// If duration is unknown or non-positive, returns 3 as a safe default for 'auto'.
    /// </summary>
    public int Resolve(double durationSeconds = 0) {
        if (!IsAuto) {
            return _value;
        }
        return ResolveAuto(durationSeconds);
    }

    /// <summary>
    /// The video length one auto part covers. Two parts per 45-minute lecture unit keeps every
    /// segment around 25 minutes including overlap - the length the transcription handles well -
    /// so a single lecture splits in 2 and a double lecture in 4.
    /// </summary>
    public const double AutoMinutesPerPart = 22.5;

    /// <summary>
    /// Computes the auto-split part count from the duration in seconds: the duration divided by
    /// <see cref="AutoMinutesPerPart"/>, rounded, at least 1. The rounding boundaries (33.75, 56.25,
    /// 78.75, 101.25 min) sit well away from the 45- and 90-minute lengths real lectures have.
    /// </summary>
    public static int ResolveAuto(double durationSeconds) {
        if (durationSeconds <= 0) return 3;
        double minutes = durationSeconds / 60.0;
        return Math.Max(1, (int)Math.Round(minutes / AutoMinutesPerPart, MidpointRounding.AwayFromZero));
    }

    public static NumberOfParts Parse(string? text) {
        if (TryParse(text, out var result)) return result;
        throw new FormatException($"cannot convert '{text}' to {nameof(NumberOfParts)}. Expected a positive integer or 'auto'.");
    }

    public static bool TryParse(string? text, out NumberOfParts result) {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (string.Equals(text, AutoLiteral, StringComparison.OrdinalIgnoreCase)) {
            result = Auto;
            return true;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) && val >= 1) {
            result = new NumberOfParts(val);
            return true;
        }
        return false;
    }

    public static implicit operator NumberOfParts(int value) => new(value);
    public static implicit operator NumberOfParts(string value) => Parse(value);
    public static implicit operator int(NumberOfParts parts) => parts.Resolve();

    public bool Equals(NumberOfParts other) => _value == other._value;
    public bool Equals(int other) => !IsAuto && _value == other;

    public override bool Equals(object? obj) => obj switch {
        NumberOfParts other => Equals(other),
        int i => Equals(i),
        string s => TryParse(s, out var p) && Equals(p),
        _ => false
    };

    public override int GetHashCode() => _value.GetHashCode();

    public override string ToString() => IsAuto ? AutoLiteral : _value.ToString(CultureInfo.InvariantCulture);

    public int CompareTo(NumberOfParts other) => Resolve().CompareTo(other.Resolve());
    public int CompareTo(int other) => Resolve().CompareTo(other);

    public static bool operator ==(NumberOfParts left, NumberOfParts right) => left.Equals(right);
    public static bool operator !=(NumberOfParts left, NumberOfParts right) => !left.Equals(right);

    public static bool operator ==(NumberOfParts left, int right) => left.Equals(right);
    public static bool operator !=(NumberOfParts left, int right) => !left.Equals(right);

    public static bool operator ==(int left, NumberOfParts right) => right.Equals(left);
    public static bool operator !=(int left, NumberOfParts right) => !right.Equals(left);

    public static bool operator <=(NumberOfParts left, int right) => left.Resolve() <= right;
    public static bool operator >=(NumberOfParts left, int right) => left.Resolve() >= right;
    public static bool operator <(NumberOfParts left, int right) => left.Resolve() < right;
    public static bool operator >(NumberOfParts left, int right) => left.Resolve() > right;

    public static bool operator <=(int left, NumberOfParts right) => left <= right.Resolve();
    public static bool operator >=(int left, NumberOfParts right) => left >= right.Resolve();
    public static bool operator <(int left, NumberOfParts right) => left < right.Resolve();
    public static bool operator >(int left, NumberOfParts right) => left > right.Resolve();
}

public sealed class NumberOfPartsTypeConverter : TypeConverter {
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || sourceType == typeof(int) || sourceType == typeof(long) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) {
        if (value is string s) return NumberOfParts.Parse(s);
        if (value is int i) return new NumberOfParts(i);
        if (value is long l) return new NumberOfParts((int)l);
        return base.ConvertFrom(context, culture, value);
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || destinationType == typeof(int) || base.CanConvertTo(context, destinationType);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) {
        if (value is NumberOfParts parts) {
            if (destinationType == typeof(string)) return parts.ToString();
            if (destinationType == typeof(int)) return parts.Resolve();
        }
        return base.ConvertTo(context, culture, value, destinationType);
    }
}

public sealed class NumberOfPartsNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter {
    public override bool CanConvert(Type objectType) => objectType == typeof(NumberOfParts) || objectType == typeof(NumberOfParts?);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        if (value is NumberOfParts parts) {
            if (parts.IsAuto) {
                writer.WriteValue(NumberOfParts.AutoLiteral);
            }
            else {
                writer.WriteValue(parts.FixedParts ?? 3);
            }
        }
        else {
            writer.WriteNull();
        }
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) {
        if (reader.TokenType == JsonToken.Integer) {
            return new NumberOfParts(Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture));
        }
        if (reader.TokenType == JsonToken.String) {
            return NumberOfParts.Parse(reader.Value?.ToString());
        }
        if (reader.TokenType == JsonToken.Null) {
            return objectType == typeof(NumberOfParts?) ? (NumberOfParts?)null : NumberOfParts.Auto;
        }
        throw new JsonSerializationException($"Unexpected token {reader.TokenType} when parsing {nameof(NumberOfParts)}.");
    }
}

public sealed class NumberOfPartsSystemTextJsonConverter : System.Text.Json.Serialization.JsonConverter<NumberOfParts> {
    public override NumberOfParts Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number) {
            return new NumberOfParts(reader.GetInt32());
        }
        if (reader.TokenType == System.Text.Json.JsonTokenType.String) {
            return NumberOfParts.Parse(reader.GetString());
        }
        throw new System.Text.Json.JsonException($"Unexpected token {reader.TokenType} when parsing {nameof(NumberOfParts)}.");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, NumberOfParts value, System.Text.Json.JsonSerializerOptions options) {
        if (value.IsAuto) {
            writer.WriteStringValue(NumberOfParts.AutoLiteral);
        }
        else {
            writer.WriteNumberValue(value.FixedParts ?? 3);
        }
    }
}
