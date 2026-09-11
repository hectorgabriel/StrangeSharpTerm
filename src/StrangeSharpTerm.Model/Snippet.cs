using System.Globalization;
using System.Text.Json.Serialization;

namespace StrangeSharpTerm.Model;

/// <summary>A saved command, optionally with placeholders to fill in before running.</summary>
public sealed record Snippet
{
    [JsonPropertyName("id"), JsonRequired]
    public NodeId Id { get; init; } = NodeId.New();

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The command, which may contain <c>{{placeholder}}</c> markers.</summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>
    /// Restricts the snippet to a folder and everything beneath it. Null means it
    /// is offered everywhere.
    /// </summary>
    [JsonPropertyName("folderID")]
    public NodeId? FolderId { get; init; }

    /// <inheritdoc cref="Tags.Normalize"/>
    [JsonPropertyName("tags"), JsonRequired]
    public IReadOnlyList<string> Tags { get; init => field = Model.Tags.Normalize(value); } = [];

    [JsonPropertyName("sortIndex"), JsonRequired]
    public int SortIndex { get; init; }

    /// <summary>Placeholder names, in order of first appearance, without duplicates.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Placeholders => SnippetTemplate.Placeholders(Command);

    [JsonIgnore]
    public bool IsParameterised => Placeholders.Count > 0;

    /// <summary>The command with placeholders replaced.</summary>
    public string Rendered(IReadOnlyDictionary<string, string> values) => SnippetTemplate.Render(Command, values);
}

/// <summary>
/// Placeholder handling for snippet commands.
///
/// <c>{{name}}</c> markers, deliberately not shell-like <c>$name</c>: a command is
/// full of legitimate <c>$</c>, and expanding those would corrupt the very
/// commands people want to save.
/// </summary>
public static class SnippetTemplate
{
    /// <summary>Placeholder names, in order of first appearance, without duplicates.</summary>
    public static IReadOnlyList<string> Placeholders(string command)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (true)
        {
            var open = command.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
                break;
            var close = command.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                break;

            var name = TrimSpacesAndTabs(command[(open + 2)..close]);
            if (name.Length > 0 && seen.Add(name))
                found.Add(name);
            index = close + 2;
        }
        return found;
    }

    /// <summary>
    /// Substitutes values into a command.
    ///
    /// A placeholder with no value is left as written rather than replaced with
    /// nothing: silently turning <c>rm -rf {{path}}</c> into <c>rm -rf</c> would be
    /// a spectacular way to lose a filesystem.
    /// </summary>
    public static string Render(string command, IReadOnlyDictionary<string, string> values)
    {
        var result = command;
        foreach (var name in Placeholders(command))
        {
            if (!values.TryGetValue(name, out var value))
                continue;
            result = result.Replace($"{{{{{name}}}}}", value, StringComparison.Ordinal);
            // Tolerate spacing inside the braces, as people write it.
            result = result.Replace($"{{{{ {name} }}}}", value, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Whether every placeholder has a non-empty value.</summary>
    public static bool IsComplete(string command, IReadOnlyDictionary<string, string> values) =>
        Placeholders(command).All(name => values.TryGetValue(name, out var value) && value.Length > 0);

    // Swift's CharacterSet.whitespaces: spaces and tabs, but not line breaks.
    private static string TrimSpacesAndTabs(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsSpaceOrTab(text[start])) start++;
        while (end > start && IsSpaceOrTab(text[end - 1])) end--;
        return text[start..end];

        static bool IsSpaceOrTab(char c) =>
            c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
    }
}
