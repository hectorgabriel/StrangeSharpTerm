using System.Text.Json;
using System.Text.Json.Serialization;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// Which folder each host has open, as <c>preferences.json</c> keeps it.
///
/// Not in the inventory, deliberately. That file is byte-identical to the Swift
/// app's and is checked against goldens the Swift code itself generates; a key
/// it has never heard of would either be dropped on the next save or break the
/// parity test, and "which folder was I last looking at" is not worth either.
/// It is also not a property of the connection — two machines can have the same
/// host open at different roots, and both are right.
///
/// Keyed by the connection's id rather than its name, so renaming a host keeps
/// its folder.
/// </summary>
public sealed record WorkspacePreferences
{
    public const string Key = "workspaces";

    /// <summary>Connection id to root, absolute on the host.</summary>
    [JsonPropertyName("roots")]
    public Dictionary<string, string> Roots { get; init; } = [];

    /// <summary>What was remembered, or nothing at all.</summary>
    public static IReadOnlyDictionary<NodeId, string> Load(string? path)
    {
        if (path is null)
            return new Dictionary<NodeId, string>();

        var stored = Preferences.Load(path).Rest?.GetValueOrDefault(Key);
        if (stored is not { ValueKind: JsonValueKind.Object } section)
            return new Dictionary<NodeId, string>();

        try
        {
            var read = section.Deserialize<WorkspacePreferences>() ?? new WorkspacePreferences();
            return read.Roots
                .Select(entry => (Parsed: NodeId.TryParse(entry.Key, out var id), Id: id, entry.Value))
                .Where(entry => entry.Parsed && entry.Value.Length > 0)
                .ToDictionary(entry => entry.Id, entry => entry.Value);
        }
        catch (JsonException)
        {
            // A section this version cannot read is not worth failing a launch
            // over. The cost is a pane that opens at the account's own
            // directory, which is where it would have opened anyway.
            return new Dictionary<NodeId, string>();
        }
    }

    /// <summary>
    /// Remembers one host's folder, leaving every other preference alone.
    ///
    /// Read back before writing, as the theme and the assistant's settings both
    /// are: two versions of this app on one synced directory is the case that
    /// needs it.
    /// </summary>
    public static void Save(string? path, NodeId host, string? root)
    {
        if (path is null)
            return;

        var preferences = Preferences.Load(path);
        var rest = preferences.Rest is null
            ? []
            : new Dictionary<string, JsonElement>(preferences.Rest);

        var roots = new Dictionary<string, string>(
            Load(path).ToDictionary(entry => entry.Key.ToString(), entry => entry.Value));

        // A closed folder is forgotten rather than remembered as empty, so the
        // file says only what is true.
        if (root is { Length: > 0 })
            roots[host.ToString()] = root;
        else
            roots.Remove(host.ToString());

        rest[Key] = JsonSerializer.SerializeToElement(new WorkspacePreferences { Roots = roots });
        (preferences with { Rest = rest }).Save(path);
    }
}
