using System.Text.Json;
using System.Text.Json.Serialization;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// The assistant's settings, as <c>preferences.json</c> keeps them.
///
/// Its own section rather than loose keys, so the file stays readable and so a
/// version that knows nothing about the assistant carries the section across
/// untouched through <see cref="Preferences.Rest"/>.
///
/// The API key is deliberately not here. It goes to the platform store, under a
/// service of its own — an API key is not a server secret and has no business in
/// the same bucket as passphrases, nor in a file beside the inventory.
/// </summary>
public sealed record AssistPreferences
{
    public const string Key = "assist";

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("sendMetrics")]
    public bool SendMetrics { get; init; } = true;

    [JsonPropertyName("sendTerminalTail")]
    public bool SendTerminalTail { get; init; } = true;

    public static AssistSettings Load(string path)
    {
        var stored = Preferences.Load(path).Rest?.GetValueOrDefault(Key);
        var read = Read(stored);

        var settings = new AssistSettings
        {
            Provider = Enum.TryParse<AssistProviderId>(read.Provider, ignoreCase: true, out var provider)
                ? provider
                : AssistProviderId.Claude,
            Model = read.Model,
            SendMetrics = read.SendMetrics,
            SendTerminalTail = read.SendTerminalTail,
        };

        // The environment has the last word, which is what redirects a
        // development build at a stub without touching anything saved.
        return settings.WithEnvironmentOverrides();
    }

    /// <summary>
    /// Writes the section back, leaving every other preference alone.
    ///
    /// Read back before writing, as the theme does: two versions of this app on
    /// one synced directory is the case that needs it.
    /// </summary>
    public static void Save(string path, AssistSettings settings)
    {
        var preferences = Preferences.Load(path);
        var rest = preferences.Rest is null
            ? []
            : new Dictionary<string, JsonElement>(preferences.Rest);

        rest[Key] = JsonSerializer.SerializeToElement(new AssistPreferences
        {
            Provider = settings.Provider.ToString().ToLowerInvariant(),
            Model = settings.Model,
            SendMetrics = settings.SendMetrics,
            SendTerminalTail = settings.SendTerminalTail,
        });

        (preferences with { Rest = rest }).Save(path);
    }

    private static AssistPreferences Read(JsonElement? stored)
    {
        if (stored is not { ValueKind: JsonValueKind.Object } section)
            return new AssistPreferences();

        try
        {
            return section.Deserialize<AssistPreferences>() ?? new AssistPreferences();
        }
        catch (JsonException)
        {
            // A section this version cannot read is not worth failing a launch
            // over. The defaults are safe: both context switches on, nothing run.
            return new AssistPreferences();
        }
    }
}
