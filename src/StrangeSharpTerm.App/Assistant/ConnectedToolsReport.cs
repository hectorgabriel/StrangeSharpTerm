using StrangeSharpTerm.Mcp;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// What <c>/mcp</c> answers with: the tool servers, and what each one offers.
///
/// Read out of the hub rather than asked of the model. A model can describe the
/// tools it was handed this turn, plausibly and without checking, and it cannot
/// see a server that failed to start — which is exactly the case somebody types
/// <c>/mcp</c> to find out about.
/// </summary>
public static class ConnectedToolsReport
{
    public static string Of(McpHub? hub)
    {
        if (hub is null || hub.Settings.Servers.Count == 0)
        {
            return "No tool servers are attached. Settings → Connected tools is where they are added.";
        }

        // From the configuration and not from the statuses: a status arrives
        // when a server has been tried, so a server that is configured and has
        // not been reached yet -- or could not be -- has none. Reporting only
        // what connected would answer "what is attached?" with silence in
        // exactly the case somebody asked.
        var statuses = hub.Statuses.ToDictionary(status => status.Config.Name, StringComparer.Ordinal);

        List<string> lines = [];
        foreach (var server in hub.Settings.Servers)
        {
            var status = statuses.GetValueOrDefault(server.Name)
                ?? new ServerStatus(server, 0, null);
            // The config's own answer to "where does this server live", rather
            // than a second one written here.
            var where = status.Config.Where;

            lines.Add($"{status.Config.Name} — {status.Summary}");
            if (where.Length > 0)
                lines.Add($"    {where}");
            if (status.Failure is { Length: > 0 } failure)
                lines.Add($"    {failure}");

            // The tools themselves, namespaced as the model is offered them, so
            // what is printed here is what it is actually given.
            foreach (var tool in hub.ToolsOf(status.Config))
            {
                var claims = tool switch
                {
                    { DestructiveHint: true } => "  (the server calls it destructive)",
                    { ReadOnlyHint: true } => "  (the server says it only reads)",
                    _ => "",
                };
                lines.Add($"    {tool.QualifiedName}{claims}");
            }

            if (status.Granted is { Length: > 0 } granted)
                lines.Add($"    {granted}");
        }

        // Said once at the end rather than beside every tool: what the server
        // claims about its own tools is a claim by the party being trusted, and
        // this app shows it without acting on it.
        lines.Add("");
        lines.Add("Every call stops and shows you the server, the tool and the exact arguments, unless you "
            + "granted that tool a standing pass.");

        return string.Join('\n', lines);
    }
}
