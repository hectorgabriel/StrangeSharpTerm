using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Mcp;

namespace StrangeSharpTerm.Mcp.Tests;

/// <summary>
/// A set of connected tools that reaches no server.
///
/// <see cref="McpHub"/> needs a live MCP client to hold a connection, and what
/// these tests are about is everything around one — namespacing, routing, the
/// grant rules and how the agent loop treats a call. This stands in for the hub
/// at the same seam the agent sees.
/// </summary>
internal sealed class FakeTools : IExternalTools
{
    private readonly Dictionary<string, McpTool> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _granted = new(StringComparer.Ordinal);

    internal List<(string Tool, string Arguments)> Called { get; } = [];

    internal Func<string, ToolReply> Answer { get; set; } = _ => new ToolReply("done");

    internal FakeTools Add(
        string server,
        string tool,
        bool readOnly = false,
        bool destructive = false,
        string destination = "metrics.example.com")
    {
        var qualified = ToolNames.Qualify(server, tool);
        _tools[qualified] = new McpTool(server, tool, qualified, $"The {tool} tool.", "{}", readOnly, destructive)
        {
            Destination = destination,
        };
        return this;
    }

    internal FakeTools GrantedAlready(string server, string tool)
    {
        _granted.Add(ToolNames.Qualify(server, tool));
        return this;
    }

    internal IReadOnlyCollection<string> Grants => _granted;

    public IReadOnlyList<AssistTool> Offered =>
        [.. _tools.Values.Select(tool => new AssistTool(tool.QualifiedName, tool.Description, tool.JsonSchema))];

    public bool Owns(string qualifiedName) => _tools.ContainsKey(qualifiedName);

    public PendingToolCall Describe(string host, string qualifiedName, string argumentsJson)
    {
        var tool = _tools[qualifiedName];
        return new PendingToolCall(
            host,
            tool.Server,
            tool.Name,
            tool.Destination,
            McpHub.Pretty(argumentsJson),
            tool.ReadOnlyHint,
            ToolGrants.MayBeGranted(tool));
    }

    public bool MayRunUnattended(string qualifiedName) =>
        _granted.Contains(qualifiedName) && !_tools[qualifiedName].DestructiveHint;

    public void Grant(string qualifiedName)
    {
        if (ToolGrants.MayBeGranted(_tools[qualifiedName]))
            _granted.Add(qualifiedName);
    }

    public Task<ToolReply> Call(string qualifiedName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        Called.Add((qualifiedName, argumentsJson));
        return Task.FromResult(Answer(qualifiedName));
    }
}

/// <summary>A gate that answers tool calls to order and remembers what it was shown.</summary>
internal sealed class RecordingToolGate(ToolApproval answer = ToolApproval.Once) : ICommandGate
{
    internal List<PendingToolCall> Asked { get; } = [];

    internal Func<PendingToolCall, ToolApproval> Answer { get; set; } = _ => answer;

    public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default)
    {
        Asked.Add(call);
        return Task.FromResult(Answer(call));
    }
}

internal sealed class SilentHost(string alias = "web-01") : IHostAccess
{
    public string Alias => alias;

    public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostSnapshot());

    public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommandOutcome(0, ""));
}
