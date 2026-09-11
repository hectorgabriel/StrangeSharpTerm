using System.Text.Json;
using System.Text.Json.Serialization;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Store;

/// <summary>
/// The inventory as written to disk.
///
/// A flat document rather than a nested tree, matching how <see cref="InventoryTree"/>
/// holds things in memory: reparenting stays a single field write, and the
/// hierarchy is derived on read. The format is the Swift app's, unchanged, so
/// either app can read the other's file.
/// </summary>
public sealed record InventoryDocument
{
    /// <summary>
    /// Bumped when the shape changes, so a future version can migrate rather than
    /// failing to decode and silently losing someone's hosts.
    /// </summary>
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("folders")]
    public required IReadOnlyList<Folder> Folders { get; init; }

    [JsonPropertyName("connections")]
    public required IReadOnlyList<Connection> Connections { get; init; }

    /// <summary>
    /// Optional on read, so a file written before snippets existed still loads.
    /// Bumping the version instead would refuse those files outright.
    /// </summary>
    [JsonPropertyName("snippets")]
    public IReadOnlyList<Snippet> Snippets { get; init; } = [];

    /// <inheritdoc cref="Snippets"/>
    [JsonPropertyName("credentials")]
    public IReadOnlyList<Credential> Credentials { get; init; } = [];

    /// <summary>
    /// Items in sort-index order, as the Swift app writes them. The sort is stable,
    /// so items sharing an index keep the order the tree holds them in, which for
    /// a tree loaded from a file is the file's own order.
    /// </summary>
    public static InventoryDocument From(InventoryTree tree) => new()
    {
        Version = CurrentVersion,
        Folders = [.. tree.Folders.Values.OrderBy(f => f.SortIndex)],
        Connections = [.. tree.Connections.Values.OrderBy(c => c.SortIndex)],
        Snippets = [.. tree.Snippets.Values.OrderBy(s => s.SortIndex)],
        Credentials = [.. tree.Credentials.Values.OrderBy(c => c.SortIndex)],
    };

    public InventoryTree ToTree() => new(Folders, Connections, Snippets, Credentials);

    /// <exception cref="UnsupportedInventoryVersionException">The file was written by a newer version.</exception>
    /// <exception cref="JsonException">The bytes are not a valid inventory.</exception>
    public static InventoryDocument Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using var json = JsonDocument.Parse(utf8Json);
        var root = json.RootElement;

        // Refuse a newer file before decoding the rest. A newer shape may not decode
        // at all, and "written by a newer version" is the error that says why.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var found)
            && found > CurrentVersion)
        {
            throw new UnsupportedInventoryVersionException(found, CurrentVersion);
        }

        return root.Deserialize<InventoryDocument>(SwiftJson.Options)
            ?? throw new JsonException("The inventory is null.");
    }

    /// <summary>The document as the Swift app would write it, byte for byte.</summary>
    public byte[] ToUtf8Json() => SwiftJson.Serialize(this);
}

/// <summary>The inventory was written by a newer version than this one.</summary>
public sealed class UnsupportedInventoryVersionException(int found, int supported)
    : Exception($"The inventory is version {found}; this version reads up to {supported}.")
{
    public int Found { get; } = found;
    public int Supported { get; } = supported;
}
