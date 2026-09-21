using System.Text.Json;
using System.Text.Json.Serialization;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// Which hosts a person has said may give <c>sudo</c> their password, as
/// <c>preferences.json</c> keeps it.
///
/// Not in the inventory, for the reason the workspace roots are not: that file
/// is byte-identical to the Swift app's, and a key it has never heard of would
/// either be dropped on its next save or break the parity test. The password
/// itself is not here either -- it stays in the platform store, where the host's
/// credential already keeps it. This is only the permission to use it.
///
/// Keyed by the connection's id rather than its name, so renaming a host keeps
/// the choice.
/// </summary>
public sealed record SudoPreferences
{
    public const string Key = "sudo";

    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; init; } = [];

    public static IReadOnlySet<NodeId> Load(string? path)
    {
        if (path is null)
            return new HashSet<NodeId>();

        var stored = Preferences.Load(path).Rest?.GetValueOrDefault(Key);
        if (stored is not { ValueKind: JsonValueKind.Object } section)
            return new HashSet<NodeId>();

        try
        {
            var read = section.Deserialize<SudoPreferences>() ?? new SudoPreferences();
            return read.Hosts
                .Select(text => (Parsed: NodeId.TryParse(text, out var id), Id: id))
                .Where(entry => entry.Parsed)
                .Select(entry => entry.Id)
                .ToHashSet();
        }
        catch (JsonException)
        {
            // Unreadable means no host has said yes, which is the safe way for
            // this particular preference to fail.
            return new HashSet<NodeId>();
        }
    }

    /// <summary>Records one host's answer, leaving every other preference alone.</summary>
    public static void Save(string? path, NodeId host, bool allowed)
    {
        if (path is null)
            return;

        var hosts = Load(path).ToHashSet();
        if (allowed)
            hosts.Add(host);
        else
            hosts.Remove(host);

        var preferences = Preferences.Load(path);
        var rest = preferences.Rest is null
            ? []
            : new Dictionary<string, JsonElement>(preferences.Rest);

        rest[Key] = JsonSerializer.SerializeToElement(new SudoPreferences
        {
            Hosts = [.. hosts.Select(id => id.ToString()).Order(StringComparer.Ordinal)],
        });
        (preferences with { Rest = rest }).Save(path);
    }
}
