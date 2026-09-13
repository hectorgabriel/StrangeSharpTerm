using System.Text.Json;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Mcp;

/// <summary>One server's state, as Settings draws it.</summary>
/// <param name="Failure">
/// Why it will not connect. Null both when it is connected and when nothing has
/// tried yet — a server nobody has attempted is not a server that failed, and
/// drawing the two the same way makes a fresh list look broken.
/// </param>
public sealed record ServerStatus(McpServerConfig Config, int ToolCount, string? Failure)
{
    public bool IsConnected { get; init; }

    /// <summary>What the row shows on the right.</summary>
    public string Summary =>
        !IsConnected ? Failure is not null ? "not connected" : "not connected yet"
        : ToolCount == 1 ? "1 tool" : $"{ToolCount} tools";

    /// <summary>What has been granted a standing pass, said in the row rather than hidden.</summary>
    public string? Granted => Config.AlwaysAllowed.Count == 0
        ? null
        : $"Runs without asking: {string.Join(", ", Config.AlwaysAllowed)}";
}

/// <summary>
/// Every connected server, and the one place a tool call is routed.
///
/// This is <see cref="IExternalTools"/> for the assistant: the agent loop asks
/// it what to offer and hands it a call, and never learns that MCP exists. The
/// namespacing is what makes routing possible — a qualified name says which
/// server a call belongs to.
/// </summary>
public sealed class McpHub : IExternalTools, IAsyncDisposable
{
    private readonly Dictionary<string, McpConnection> _connections = new(StringComparer.Ordinal);
    private readonly List<ServerStatus> _statuses = [];
    private readonly Lock _gate = new();
    private McpSettings _settings;
    private readonly Action<McpSettings>? _save;
    private readonly ISecretStore? _store;

    /// <param name="save">
    /// Called when a standing pass is granted, because a grant is a setting and
    /// outlives the pane that granted it.
    /// </param>
    /// <param name="store">
    /// Where bearer tokens and sign-ins are kept. Null means the platform's own,
    /// which is what the app uses and what a test must not touch.
    /// </param>
    public McpHub(McpSettings settings, Action<McpSettings>? save = null, ISecretStore? store = null)
    {
        _settings = settings;
        _save = save;
        _store = store;
    }

    public McpSettings Settings
    {
        get
        {
            lock (_gate)
                return _settings;
        }
    }

    /// <summary>What each configured server is doing, for Settings to draw.</summary>
    public IReadOnlyList<ServerStatus> Statuses
    {
        get
        {
            lock (_gate)
                return [.. _statuses];
        }
    }

    /// <summary>A server connected or failed, so a sheet can fill its row in as they arrive.</summary>
    public event EventHandler<ServerStatus>? Changed;

    /// <summary>
    /// Replaces the configuration and remembers it.
    ///
    /// Nothing reconnects here: adding a server and connecting to it are
    /// separate, because the first is instant and the second can take a while or
    /// open a browser, and a sheet that froze on Save would be worse than one
    /// whose row fills in a moment later.
    /// </summary>
    public void Use(McpSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            // Statuses for servers that are gone go with them, so a removed one
            // does not linger in a sheet as a row nothing can act on.
            var known = settings.Servers.Select(server => server.Id).ToHashSet();
            _statuses.RemoveAll(status => !known.Contains(status.Config.Id));
        }
        _save?.Invoke(settings);
    }

    /// <summary>
    /// Connects everything switched on, in parallel, and never throws.
    ///
    /// A server that will not start is a row that says so, not a failure that
    /// stops the others: one broken entry in the list must not cost the rest.
    /// </summary>
    public async Task Connect(CancellationToken cancellationToken = default)
    {
        await Disconnect();

        var configured = Settings.Usable;
        var opened = await Task.WhenAll(configured.Select(async config =>
        {
            try
            {
                var connection = await McpConnection.Open(config, store: _store, cancellationToken: cancellationToken);
                return (config, Connection: (McpConnection?)connection, Failure: (string?)null);
            }
            catch (OperationCanceledException)
            {
                return (config, Connection: null, Failure: "Stopped.");
            }
            catch (Exception e)
            {
                // Whatever it was, it is a sentence in a row rather than an
                // exception out of here: one broken entry must not cost the rest.
                return (config, Connection: null, Failure: e.Message);
            }
        }));

        lock (_gate)
        {
            _statuses.Clear();
            foreach (var (config, connection, failure) in opened)
            {
                if (connection is not null)
                    _connections[Key(config)] = connection;
                _statuses.Add(new ServerStatus(config, connection?.Tools.Count ?? 0, failure)
                {
                    IsConnected = connection is not null,
                });
            }
        }

        foreach (var status in Statuses)
            Changed?.Invoke(this, status);
    }

    public async Task Disconnect()
    {
        McpConnection[] open;
        lock (_gate)
        {
            open = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (var connection in open)
            await connection.DisposeAsync();
    }

    public IReadOnlyList<AssistTool> Offered
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _connections.Values
                        .SelectMany(connection => connection.Tools)
                        .Select(tool => new AssistTool(tool.QualifiedName, Describe(tool), tool.JsonSchema)),
                ];
            }
        }
    }

    public bool Owns(string qualifiedName) => Find(qualifiedName) is not null;

    /// <summary>What one server offers, for a settings row or a listing to show.</summary>
    public IReadOnlyList<McpTool> ToolsOf(McpServerConfig server)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(Key(server), out var connection) ? connection.Tools : [];
        }
    }

    public PendingToolCall Describe(string host, string qualifiedName, string argumentsJson)
    {
        var found = Find(qualifiedName);
        var tool = found?.Tool;
        return new PendingToolCall(
            host,
            tool?.Server ?? "an unknown server",
            tool?.Name ?? qualifiedName,
            tool?.Destination ?? "somewhere unknown",
            Pretty(argumentsJson),
            tool?.ReadOnlyHint ?? false,
            tool is not null && ToolGrants.MayBeGranted(tool));
    }

    public bool MayRunUnattended(string qualifiedName) =>
        Find(qualifiedName) is { } found && ToolGrants.MayRunUnattended(Config(found.Tool.Server), found.Tool);

    public void Grant(string qualifiedName)
    {
        if (Find(qualifiedName) is not { } found)
            return;

        lock (_gate)
        {
            var config = Config(found.Tool.Server);
            var granted = ToolGrants.Grant(config, found.Tool);
            if (granted == config)
                return;

            _settings = _settings.Upsert(granted);
            for (var i = 0; i < _statuses.Count; i++)
            {
                if (_statuses[i].Config.Id == granted.Id)
                    _statuses[i] = _statuses[i] with { Config = granted };
            }
        }

        _save?.Invoke(Settings);
    }

    public async Task<ToolReply> Call(
        string qualifiedName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        if (Find(qualifiedName) is not { } found)
            return new ToolReply($"There is no tool called {qualifiedName}.", Failed: true);

        var outcome = await found.Connection.Call(found.Tool.Name, argumentsJson, cancellationToken);
        return new ToolReply(outcome.Output, outcome.Failed);
    }

    /// <summary>
    /// The description a provider is given.
    ///
    /// It names the server, because a model choosing between two servers' tools
    /// should be able to tell them apart, and a bare "search" cannot be chosen
    /// between.
    /// </summary>
    private static string Describe(McpTool tool)
    {
        var description = tool.Description.Length > 0 ? tool.Description : $"The {tool.Name} tool.";
        return $"[{tool.Server}] {description}";
    }

    /// <summary>
    /// The arguments as a person reads them, which is how the gate shows them.
    ///
    /// Public because anything drawing a pending call needs the same rendering:
    /// the bar shows exactly what is about to be sent, and two formattings of it
    /// would be two answers to the same question.
    /// </summary>
    public static string Pretty(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, Layout);
        }
        catch (JsonException)
        {
            // Unparseable arguments are shown exactly as they arrived: whatever
            // is about to be sent is what a person needs to see.
            return json;
        }
    }

    private sealed record Found(McpConnection Connection, McpTool Tool);

    private Found? Find(string qualifiedName)
    {
        lock (_gate)
        {
            foreach (var connection in _connections.Values)
            {
                if (connection.Tools.FirstOrDefault(tool =>
                    string.Equals(tool.QualifiedName, qualifiedName, StringComparison.Ordinal)) is { } tool)
                {
                    return new Found(connection, tool);
                }
            }
            return null;
        }
    }

    private McpServerConfig Config(string server) =>
        Settings.ByName(server) ?? new McpServerConfig { Name = server };

    private static string Key(McpServerConfig config) => config.Id.Value.ToString();

    public async ValueTask DisposeAsync() => await Disconnect();

    private static readonly JsonSerializerOptions Layout = new() { WriteIndented = true };
}
