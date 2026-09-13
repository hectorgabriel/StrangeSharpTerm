namespace StrangeSharpTerm.Mcp;

/// <summary>
/// One tool a connected server offers.
/// </summary>
/// <param name="QualifiedName">What a provider is offered: <c>grafana__query_range</c>.</param>
/// <param name="ReadOnlyHint">
/// The server's own claim that this tool only reads.
///
/// A claim by the party being trusted. It is shown and never acted on — exactly
/// the role the command denylist plays beside the allowlist, which is to make a
/// person better informed without granting anything.
/// </param>
/// <param name="DestructiveHint">
/// The server's own claim that this tool destroys something. This one <em>is</em>
/// acted on, in the only direction a claim like that can safely be believed: a
/// tool the server calls destructive cannot be given a standing pass at all.
/// </param>
public sealed record McpTool(
    string Server,
    string Name,
    string QualifiedName,
    string Description,
    string JsonSchema,
    bool ReadOnlyHint = false,
    bool DestructiveHint = false)
{
    /// <summary>What the approval bar says about where the call goes.</summary>
    public required string Destination { get; init; }
}

/// <summary>
/// Whether a tool call may go ahead without asking.
///
/// There is no <c>CommandPolicy</c> equivalent here and there cannot be. That
/// classifier works because a shell command is a string it can parse;
/// <c>create_incident</c> with a JSON body is an opaque name written by the same
/// server that would carry out the call. So the pass is granted per tool, by a
/// person, at the gate — and that is the only way onto the list.
/// </summary>
public static class ToolGrants
{
    /// <summary>Whether this call runs without asking.</summary>
    public static bool MayRunUnattended(McpServerConfig server, McpTool tool) =>
        !tool.DestructiveHint
        && server.AlwaysAllowed.Contains(tool.Name, StringComparer.Ordinal);

    /// <summary>
    /// Whether Always allow may even be offered for this tool.
    ///
    /// A tool the server itself calls destructive cannot be given a standing
    /// pass. Believing that claim only ever narrows what can happen, which is
    /// the one direction a claim from the party being trusted is safe in.
    /// </summary>
    public static bool MayBeGranted(McpTool tool) => !tool.DestructiveHint;

    /// <summary>Grants a standing pass, if the tool is allowed one.</summary>
    public static McpServerConfig Grant(McpServerConfig server, McpTool tool) =>
        !MayBeGranted(tool) || server.AlwaysAllowed.Contains(tool.Name, StringComparer.Ordinal)
            ? server
            : server with { AlwaysAllowed = [.. server.AlwaysAllowed, tool.Name] };

    /// <summary>Takes one back. Settings shows what has been granted and lets you do this.</summary>
    public static McpServerConfig Revoke(McpServerConfig server, string tool) =>
        server with
        {
            AlwaysAllowed = [.. server.AlwaysAllowed.Where(granted => !string.Equals(granted, tool, StringComparison.Ordinal))],
        };
}
