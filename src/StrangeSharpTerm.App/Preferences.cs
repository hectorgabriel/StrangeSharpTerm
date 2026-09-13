using System.Text.Json;
using System.Text.Json.Serialization;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Store;

namespace StrangeSharpTerm.App;

/// <summary>
/// The few things the app remembers about itself, as opposed to about a host.
///
/// The Swift app kept these in <c>UserDefaults</c>, so there is no file to stay
/// compatible with — unlike the inventory, this format is ours. It sits beside
/// the inventory rather than in a directory of its own, which means the
/// environment variable that points a test run at a throwaway inventory points
/// its preferences somewhere throwaway too.
/// </summary>
public sealed record Preferences
{
    public const string FileName = "preferences.json";

    /// <summary>The chosen theme, by id. Null until someone chooses one.</summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    /// <summary>
    /// Anything else the file holds, kept so that writing one preference does
    /// not delete another. Two versions of this app on one synced directory is
    /// the case that needs it.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Rest { get; init; }

    public static string DefaultPath()
    {
        var inventory = Environment.GetEnvironmentVariable(InventoryLoader.InventoryVariable)
            ?? InventoryStore.DefaultPath();
        return Path.Combine(Path.GetDirectoryName(inventory) ?? ".", FileName);
    }

    /// <summary>
    /// What was remembered, or the defaults. A file that cannot be read is not
    /// worth failing a launch over: the worst case is a window in the wrong
    /// theme, and the next save writes a readable one.
    /// </summary>
    public static Preferences Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new Preferences()
                : new Preferences();
        }
        catch (Exception)
        {
            return new Preferences();
        }
    }

    /// <summary>
    /// Writes them out, and says nothing if it cannot. Losing a preference is not
    /// a reason to interrupt whatever the user was actually doing.
    /// </summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Layout));
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"saving {path} failed: {e.Message}");
        }
    }

    private static readonly JsonSerializerOptions Layout = new() { WriteIndented = true };
}
