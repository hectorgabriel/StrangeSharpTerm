using System.Text.Json;
using StrangeSharpTerm.Mcp;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// Connected tools, in the same preferences file as the theme and the
/// assistant.
///
/// Its own section, so a version that knows nothing about connected tools
/// carries them across untouched through <see cref="Preferences.Rest"/> — the
/// same arrangement <see cref="AssistPreferences"/> makes.
/// </summary>
public static class McpPreferences
{
    public static McpSettings Load(string path) =>
        McpDocument.Read(Preferences.Load(path).Rest?.GetValueOrDefault(McpDocument.Key));

    /// <summary>Writes the section back, leaving every other preference alone.</summary>
    public static void Save(string path, McpSettings settings)
    {
        var preferences = Preferences.Load(path);
        var rest = preferences.Rest is null
            ? []
            : new Dictionary<string, JsonElement>(preferences.Rest);

        rest[McpDocument.Key] = JsonSerializer.SerializeToElement(McpDocument.From(settings));
        (preferences with { Rest = rest }).Save(path);
    }
}
