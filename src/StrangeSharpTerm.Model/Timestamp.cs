using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrangeSharpTerm.Model;

/// <summary>
/// A point in time as Swift's <c>Date</c> stores it: seconds since
/// 2001-01-01T00:00:00Z, as a double. That is how the inventory file records it.
///
/// Kept in that form rather than converted to <see cref="DateTimeOffset"/> on
/// read. A DateTimeOffset's 100-nanosecond ticks cannot hold every double, so a
/// value converted on load would come back different on save, and an inventory
/// written by the Swift app would stop round-tripping byte for byte.
/// </summary>
[JsonConverter(typeof(TimestampJsonConverter))]
public readonly record struct Timestamp(double SecondsSinceReferenceDate)
{
    public static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Timestamp FromDateTimeOffset(DateTimeOffset value) =>
        new((value - ReferenceDate).Ticks / (double)TimeSpan.TicksPerSecond);

    public DateTimeOffset ToDateTimeOffset() =>
        ReferenceDate + TimeSpan.FromTicks((long)Math.Round(SecondsSinceReferenceDate * TimeSpan.TicksPerSecond));
}

// Goes through the serializer rather than reading and writing the number directly,
// so any double converter in the options applies to timestamps too.
internal sealed class TimestampJsonConverter : JsonConverter<Timestamp>
{
    public override Timestamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<double>(ref reader, options));

    public override void Write(Utf8JsonWriter writer, Timestamp value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.SecondsSinceReferenceDate, options);
}
