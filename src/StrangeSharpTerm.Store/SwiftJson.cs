using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrangeSharpTerm.Store;

/// <summary>
/// JSON exactly as the Swift app's <c>JSONEncoder</c> writes it with
/// <c>[.prettyPrinted, .sortedKeys]</c>, so an inventory it wrote survives a load
/// and save here byte for byte.
///
/// Every rule was read off the real encoder's output, not its documentation; see
/// <c>build/swift-parity</c> and the goldens it generates. Two-space indent;
/// <c>"key" : value</c>; keys sorted by Unicode scalar value; an empty container
/// as its open bracket, a blank line, and its close; <c>/</c> escaped; control
/// characters as <c>\u00xx</c> unless they have a short escape; everything else,
/// non-ASCII included, raw; no trailing newline.
/// </summary>
internal static class SwiftJson
{
    /// <summary>2^53. Swift writes a double beyond this in exponent form, even when it is integral.</summary>
    private const double LargestExactInteger = 9007199254740992.0;

    public static JsonSerializerOptions Options { get; } = new()
    {
        // Swift's synthesized Codable omits a nil optional rather than writing null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // and refuses null where the type is not optional.
        RespectNullableAnnotations = true,
        Converters = { new MarkedDoubleConverter() },
    };

    public static byte[] Serialize<T>(T value)
    {
        using var document = JsonSerializer.SerializeToDocument(value, Options);
        var output = new StringBuilder();
        WriteValue(output, document.RootElement, depth: 0);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static void WriteValue(StringBuilder output, JsonElement value, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteContainer(output, '{', '}', depth,
                    value.EnumerateObject().OrderBy(property => property.Name, ScalarOrder.Instance).ToArray(),
                    (property, childDepth) =>
                    {
                        WriteString(output, property.Name);
                        output.Append(" : ");
                        WriteValue(output, property.Value, childDepth);
                    });
                break;
            case JsonValueKind.Array:
                WriteContainer(output, '[', ']', depth, value.EnumerateArray().ToArray(),
                    (item, childDepth) => WriteValue(output, item, childDepth));
                break;
            case JsonValueKind.String:
                WriteString(output, value.GetString()!);
                break;
            case JsonValueKind.Number:
                output.Append(FormatNumber(value.GetRawText()));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            default:
                output.Append("null");
                break;
        }
    }

    private static void WriteContainer<T>(
        StringBuilder output, char open, char close, int depth, T[] items, Action<T, int> writeItem)
    {
        output.Append(open).Append('\n');
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0)
                output.Append(",\n");
            output.Append(' ', 2 * (depth + 1));
            writeItem(items[i], depth + 1);
        }
        // Written even when the container is empty, which is where the blank line comes from.
        output.Append('\n').Append(' ', 2 * depth).Append(close);
    }

    private static void WriteString(StringBuilder output, string text)
    {
        output.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '/': output.Append("\\/"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case < ' ': output.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)); break;
                default: output.Append(c); break;
            }
        }
        output.Append('"');
    }

    /// <summary>
    /// An integer literal is written as it is. Anything carrying a decimal point or
    /// an exponent is a double (see <see cref="MarkedDoubleConverter"/>) and gets
    /// Swift's layout.
    /// </summary>
    private static string FormatNumber(string raw) =>
        raw.AsSpan().IndexOfAny(".eE") < 0
            ? raw
            : FormatDouble(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));

    /// <summary>
    /// Swift's <c>Double.description</c> with a trailing ".0" removed, which is what
    /// its encoder writes: the shortest digits that round-trip, laid out in decimal
    /// unless the value is beyond 2^53 or has more than three zeros after the point.
    /// </summary>
    internal static string FormatDouble(double value)
    {
        if (value == 0)
            return double.IsNegative(value) ? "-0" : "0";

        // Shortest round-trip digits, in whatever layout .NET chooses: "13.5",
        // "0.0001", "1E-05", "1.5E+17", "10000000000000000".
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = shortest.IndexOf('E');
        var mantissa = exponentAt < 0 ? shortest : shortest[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.Parse(shortest[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        // Re-express as 0.DIGITS × 10^point, with no leading or trailing zeros in DIGITS.
        var dot = mantissa.IndexOf('.');
        var integerPart = dot < 0 ? mantissa : mantissa[..dot];
        var allDigits = dot < 0 ? mantissa : integerPart + mantissa[(dot + 1)..];
        var digits = allDigits.TrimStart('0');
        var point = integerPart.Length + exponent - (allDigits.Length - digits.Length);
        digits = digits.TrimEnd('0');

        var sign = value < 0 ? "-" : "";
        if (point < -3 || Math.Abs(value) > LargestExactInteger)
        {
            var scientific = point - 1;
            var significand = digits.Length == 1 ? digits : $"{digits[0]}.{digits[1..]}";
            return $"{sign}{significand}e{(scientific < 0 ? '-' : '+')}{Math.Abs(scientific):00}";
        }
        if (point <= 0)
            return $"{sign}0.{new string('0', -point)}{digits}";
        if (point >= digits.Length)
            return $"{sign}{digits}{new string('0', point - digits.Length)}";
        return $"{sign}{digits[..point]}.{digits[point..]}";
    }

    /// <summary>
    /// Writes a double with a decimal point or an exponent, always. Swift lays out a
    /// large integral double differently from an integer of the same value, so
    /// the writer has to be able to tell them apart after the fact. "R" digits
    /// round-trip exactly, so nothing is lost on the way through.
    /// </summary>
    private sealed class MarkedDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            // Swift's encoder refuses these too; JSON has no way to spell them.
            if (!double.IsFinite(value))
                throw new JsonException($"{value} cannot be written as JSON.");

            var text = value.ToString("R", CultureInfo.InvariantCulture);
            writer.WriteRawValue(text.AsSpan().IndexOfAny(".E") < 0 ? text + ".0" : text);
        }
    }

    /// <summary>
    /// Swift sorts keys by Unicode scalar value. UTF-16 ordinal order agrees with
    /// that everywhere except beyond the Basic Multilingual Plane, so this compares
    /// runes rather than chars.
    /// </summary>
    private sealed class ScalarOrder : IComparer<string>
    {
        public static readonly ScalarOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var left = x!.EnumerateRunes();
            var right = y!.EnumerateRunes();
            while (true)
            {
                var hasLeft = left.MoveNext();
                var hasRight = right.MoveNext();
                if (!hasLeft || !hasRight)
                    return hasLeft.CompareTo(hasRight);
                var order = left.Current.Value.CompareTo(right.Current.Value);
                if (order != 0)
                    return order;
            }
        }
    }
}
