namespace StrangeSharpTerm.Mcp;

/// <summary>
/// Which servers are attached, and where their tools may be used.
///
/// The two switches have different defaults on purpose, and the difference is
/// the whole of what this type is for.
/// </summary>
public sealed record McpSettings
{
    public IReadOnlyList<McpServerConfig> Servers { get; init; } = [];

    /// <summary>
    /// Assistant panes, on.
    ///
    /// Connecting a server is already the deliberate act, and every call still
    /// asks. Making you turn it on twice would be ceremony rather than safety.
    /// </summary>
    public bool OfferInPanes { get; init; } = true;

    /// <summary>
    /// Orchestrated runs, off.
    ///
    /// A fan-out multiplies everything. Every host in a run gets the <em>same</em>
    /// tools pointed at the same place, so one instruction can become one write
    /// per host, and the gate that would have caught it is eight approvals deep
    /// in a queue nobody reads properly by the fourth.
    /// </summary>
    public bool OfferInRuns { get; init; }

    /// <summary>The servers that are switched on and configured well enough to try.</summary>
    public IReadOnlyList<McpServerConfig> Usable =>
        [.. Servers.Where(server => server.IsEnabled && server.IsUsable)];

    public McpServerConfig? ByName(string name) =>
        Servers.FirstOrDefault(server => string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or replaces a server, keeping the rest in order.</summary>
    public McpSettings Upsert(McpServerConfig server) =>
        this with
        {
            Servers = Servers.Any(existing => existing.Id == server.Id)
                ? [.. Servers.Select(existing => existing.Id == server.Id ? server : existing)]
                : [.. Servers, server],
        };

    /// <summary>
    /// Removes a server, and its grants with it.
    ///
    /// The grants live on the server, so this is automatic — which is the point:
    /// re-adding a server by the same name must not inherit permissions granted
    /// to whatever was there before.
    /// </summary>
    public McpSettings Remove(McpServerConfig server) =>
        this with { Servers = [.. Servers.Where(existing => existing.Id != server.Id)] };
}
