using System.Text.Json.Serialization;

namespace StrangeSharpTerm.Model;

/// <summary>A grouping node. Carries partial settings that cascade to everything beneath it.</summary>
public sealed record Folder
{
    [JsonPropertyName("id"), JsonRequired]
    public NodeId Id { get; init; } = NodeId.New();

    [JsonPropertyName("parentID")]
    public NodeId? ParentId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("settings"), JsonRequired]
    public ConnectionSettings Settings { get; init; } = ConnectionSettings.Empty;

    /// <inheritdoc cref="Tags.Normalize"/>
    [JsonPropertyName("tags"), JsonRequired]
    public IReadOnlyList<string> Tags { get; init => field = Model.Tags.Normalize(value); } = [];

    [JsonPropertyName("sortIndex"), JsonRequired]
    public int SortIndex { get; init; }
}

/// <summary>A single server.</summary>
public sealed record Connection
{
    [JsonPropertyName("id"), JsonRequired]
    public NodeId Id { get; init; } = NodeId.New();

    [JsonPropertyName("parentID")]
    public NodeId? ParentId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The address to connect to, exactly as the user gave it. Never rewritten or
    /// "normalised": it may be an alias that means something to them.
    /// </summary>
    [JsonPropertyName("hostname")]
    public required string Hostname { get; init; }

    [JsonPropertyName("settings"), JsonRequired]
    public ConnectionSettings Settings { get; init; } = ConnectionSettings.Empty;

    /// <inheritdoc cref="Tags.Normalize"/>
    [JsonPropertyName("tags"), JsonRequired]
    public IReadOnlyList<string> Tags { get; init => field = Model.Tags.Normalize(value); } = [];

    [JsonPropertyName("sortIndex"), JsonRequired]
    public int SortIndex { get; init; }

    [JsonPropertyName("lastConnectedAt")]
    public Timestamp? LastConnectedAt { get; init; }

    /// <summary>
    /// True when this record mirrors a <c>Host</c> block in <c>~/.ssh/config</c>
    /// rather than being owned by us.
    /// </summary>
    [JsonPropertyName("importedFromSSHConfig"), JsonRequired]
    public bool ImportedFromSshConfig { get; init; }
}

/// <summary>A connection with inheritance already applied, ready to hand to the transport.</summary>
/// <param name="InheritanceChain">
/// Folders that contributed, outermost first. The UI uses this to explain
/// <em>why</em> a value is what it is ("Username inherited from Production").
/// </param>
public sealed record ResolvedConnection(
    Connection Connection,
    ResolvedSettings Settings,
    IReadOnlyList<NodeId> InheritanceChain);

internal static class Tags
{
    /// <summary>
    /// A set in meaning, held as a list so the order read from a file is the order
    /// written back. Swift writes a <c>Set</c> in whatever order it happens to
    /// iterate; sorting here would make every Swift-written file differ on its
    /// first save. Duplicates are dropped, as a set would, keeping the first.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string> tags) =>
        [.. tags.Distinct(StringComparer.Ordinal)];
}
