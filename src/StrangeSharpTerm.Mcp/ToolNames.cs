using System.Text;

namespace StrangeSharpTerm.Mcp;

/// <summary>
/// What a connected tool is called once it reaches a provider.
///
/// Namespaced by server — <c>grafana__query_range</c> — because two servers may
/// each have a <c>search</c>, and a provider handed a duplicate name rejects the
/// whole request rather than the offending tool. Sanitised and length-capped for
/// the same reason: a name a provider will not accept fails the turn, not the
/// call.
/// </summary>
public static class ToolNames
{
    /// <summary>What separates the server from the tool. Two underscores, as the Swift app had it.</summary>
    public const string Separator = "__";

    /// <summary>
    /// The ceiling both providers impose. Long enough that it is rarely
    /// reached, short enough that reaching it is not a 400.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>The name a provider is offered.</summary>
    public static string Qualify(string server, string tool)
    {
        var prefix = Sanitise(server);
        var name = Sanitise(tool);

        // The tool's own name is the half worth keeping whole: two servers with
        // the same prefix are still told apart by it, and a truncated tool name
        // is one the model cannot ask for.
        var room = MaxLength - Separator.Length - name.Length;
        if (room < 1)
            return Cap(name);

        return string.Concat(prefix.Length <= room ? prefix : prefix[..room], Separator, name);
    }

    /// <summary>
    /// Splits a qualified name back into its halves, or null when it carries no
    /// prefix. What the router uses to decide which server a call belongs to.
    /// </summary>
    public static (string Server, string Tool)? Split(string qualified)
    {
        var at = qualified.IndexOf(Separator, StringComparison.Ordinal);
        return at <= 0 || at + Separator.Length >= qualified.Length
            ? null
            : (qualified[..at], qualified[(at + Separator.Length)..]);
    }

    /// <summary>
    /// Letters, digits, underscores and hyphens, which is the intersection both
    /// providers accept. Anything else becomes an underscore rather than being
    /// dropped, so two tools differing only in punctuation stay different.
    /// </summary>
    internal static string Sanitise(string name)
    {
        var clean = new StringBuilder(name.Length);
        foreach (var c in name)
            clean.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? char.ToLowerInvariant(c) : '_');

        // Not trimmed. An underscore at either end is legal in both providers'
        // name patterns, and trimming it makes "café" and "caf" the same name --
        // which is exactly the collision namespacing exists to prevent.
        var sanitised = clean.ToString();
        return sanitised.Length == 0 ? "tool" : Cap(sanitised);
    }

    private static string Cap(string name) => name.Length <= MaxLength ? name : name[..MaxLength];
}
