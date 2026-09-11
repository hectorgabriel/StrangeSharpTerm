using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrangeSharpTerm.Model;

/// <summary>
/// Stable identity for anything in the connection inventory.
///
/// A wrapper rather than a bare <see cref="Guid"/>, so an inventory id cannot be
/// confused with any other Guid in the program.
/// </summary>
[JsonConverter(typeof(NodeIdJsonConverter))]
public readonly record struct NodeId(Guid Value)
{
    public static NodeId New() => new(Guid.NewGuid());

    /// <summary>
    /// Fails rather than minting a fresh id, so a malformed value surfaces as an
    /// error instead of a phantom node. Accepts either case, as Swift's
    /// <c>UUID(uuidString:)</c> does.
    /// </summary>
    public static bool TryParse(string? text, out NodeId id)
    {
        var parsed = Guid.TryParseExact(text, "D", out var guid);
        id = new NodeId(guid);
        return parsed;
    }

    /// <summary>Uppercase, as Swift's <c>uuidString</c> writes it into the inventory file.</summary>
    public override string ToString() => Value.ToString("D").ToUpperInvariant();
}

/// <summary>
/// Swift's synthesized <c>Codable</c> wraps the UUID in an object keyed by the
/// field name, so an id on disk is <c>{"rawValue": "…"}</c>, not a bare string.
/// </summary>
internal sealed class NodeIdJsonConverter : JsonConverter<NodeId>
{
    private const string RawValue = "rawValue";

    public override NodeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A node id must be an object with a rawValue.");

        NodeId? id = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var isRawValue = reader.ValueTextEquals(RawValue);
            reader.Read();
            if (!isRawValue)
            {
                reader.Skip();
                continue;
            }
            if (reader.TokenType != JsonTokenType.String || !NodeId.TryParse(reader.GetString(), out var parsed))
                throw new JsonException("A node id's rawValue is not a UUID.");
            id = parsed;
        }
        return id ?? throw new JsonException("A node id is missing its rawValue.");
    }

    public override void Write(Utf8JsonWriter writer, NodeId value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(RawValue, value.ToString());
        writer.WriteEndObject();
    }
}
