using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Mcp;

/// <summary>What a tool call produced.</summary>
/// <param name="Failed">
/// The tool reported its own failure. Distinct from the call not happening: the
/// model is told which, because "the server said no" and "the server was not
/// reachable" lead somewhere different.
/// </param>
public sealed record ToolOutcome(string Output, bool Failed = false);

/// <summary>
/// One connected server.
///
/// A thin wrapper over the official SDK's client: the handshake, the transports
/// and the protocol are its business. What is ours is everything around them —
/// which command to actually launch, what the tools are called once they reach a
/// provider, and what a person is shown before a call goes out. See
/// docs/adr/0007.
/// </summary>
public sealed class McpConnection : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly McpServerConfig _config;

    private McpConnection(McpClient client, McpServerConfig config, IReadOnlyList<McpTool> tools)
    {
        _client = client;
        _config = config;
        Tools = tools;
    }

    public McpServerConfig Config => _config;

    /// <summary>What this server offers, already namespaced.</summary>
    public IReadOnlyList<McpTool> Tools { get; }

    /// <summary>
    /// Connects, handshakes and lists the tools.
    ///
    /// The listing happens here rather than lazily because a server with no
    /// usable tools is a server worth saying so about in Settings, and because
    /// the count is what the row shows.
    /// </summary>
    public static async Task<McpConnection> Open(
        McpServerConfig config,
        McpClientOptions? options = null,
        ISecretStore? store = null,
        CancellationToken cancellationToken = default)
    {
        if (config.Problem is { } problem)
            throw new McpException(problem);

        IClientTransport transport;
        try
        {
            transport = Transport(config, store);
        }
        catch (Exception e) when (e is not McpException)
        {
            throw new McpException($"{config.Name} could not be set up. ({e.Message})", e);
        }

        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new McpException(Explain(config, e), e);
        }

        try
        {
            var listed = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return new McpConnection(client, config, [.. listed.Select(tool => Describe(config, tool))]);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await client.DisposeAsync();
            throw new McpException($"{config.Name} connected but would not list its tools. ({e.Message})", e);
        }
    }

    /// <summary>
    /// Calls a tool by its bare name and returns what it said as text.
    ///
    /// Whatever comes back is data from a third party. It is redacted and
    /// truncated by the caller before it reaches a provider, exactly as command
    /// output is.
    /// </summary>
    public async Task<ToolOutcome> Call(
        string tool,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.CallToolAsync(
                tool,
                Arguments(argumentsJson),
                cancellationToken: cancellationToken);

            var text = string.Join(
                "\n",
                result.Content
                    .OfType<TextContentBlock>()
                    .Select(block => block.Text)
                    .Where(line => line.Length > 0));

            return new ToolOutcome(
                text.Length > 0 ? text : "(the tool returned nothing)",
                result.IsError ?? false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // A server that died mid-call, or a tool that is not there any more.
            // The model is told, because it is a fact about the world it is
            // reasoning about.
            return new ToolOutcome($"The tool could not be called: {e.Message}", Failed: true);
        }
    }

    private static IClientTransport Transport(McpServerConfig config, ISecretStore? store) => config.Transport switch
    {
        McpTransport.Http => Http(config, store),
        _ => new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            // Resolved rather than handed over bare: an app launched from Finder
            // inherits launchd's PATH, not a shell's, so npx and uvx are
            // otherwise invisible. See CommandPath.
            Command = CommandPath.Resolve(config.Command),
            Arguments = [.. config.Arguments],
            EnvironmentVariables = config.Environment.Count == 0
                ? null
                : config.Environment.ToDictionary(entry => entry.Key, entry => (string?)entry.Value),
        }),
    };

    /// <summary>
    /// An HTTP server, authenticated however it has been set up.
    ///
    /// A pasted bearer token wins: someone who supplied one has said how their
    /// server authenticates, and starting a discovery underneath them would be
    /// second-guessing it. Otherwise OAuth is configured but not interactive --
    /// a request failing mid-turn is the worst possible moment to seize the
    /// screen, so the transport renews silently or gives up, and the visible
    /// login is something a person starts from Settings.
    /// </summary>
    private static HttpClientTransport Http(McpServerConfig config, ISecretStore? store)
    {
        var credentials = McpTokens.Read(config, store);
        var options = new HttpClientTransportOptions
        {
            Name = config.Name,
            Endpoint = new Uri(config.Url),
        };

        if (credentials.HasBearer)
        {
            options.AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = $"Bearer {credentials.Bearer}",
            };
        }
        else
        {
            options.OAuth = McpSignIn.For(config, credentials, interactive: false, store: store);
        }

        return new HttpClientTransport(options);
    }

    private static McpTool Describe(McpServerConfig config, McpClientTool tool) => new(
        config.Name,
        tool.Name,
        ToolNames.Qualify(config.Name, tool.Name),
        tool.Description ?? "",
        tool.JsonSchema.ToString(),
        // Both of these are the server's own claims about itself. What is done
        // with each is the difference -- see ToolGrants.
        ReadOnlyHint: tool.ProtocolTool.Annotations?.ReadOnlyHint ?? false,
        DestructiveHint: tool.ProtocolTool.Annotations?.DestructiveHint ?? false)
    {
        Destination = Destination(config),
    };

    /// <summary>
    /// Where a call to this server goes, in words.
    ///
    /// A local server is not a network destination at all, and saying so is as
    /// much a part of the choice as naming the host an HTTP one reaches.
    /// </summary>
    internal static string Destination(McpServerConfig config) =>
        config.Transport == McpTransport.Http
            && Uri.TryCreate(config.Url, UriKind.Absolute, out var uri)
                ? uri.Host
                : "this machine";

    private static IReadOnlyDictionary<string, object?>? Arguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            return document.RootElement.EnumerateObject()
                .ToDictionary(field => field.Name, field => (object?)field.Value.Clone());
        }
        catch (JsonException)
        {
            // A model that sent something unparseable gets told so by the
            // server, which is a better error than one invented here.
            return null;
        }
    }

    private static string Explain(McpServerConfig config, Exception e) => config.Transport switch
    {
        McpTransport.Http => $"{config.Name} at {Destination(config)} could not be reached. ({e.Message})",
        _ when !CommandPath.Exists(config.Command) =>
            $"{config.Name} could not start: {config.Command} is not on the path. "
            + $"Looked in {string.Join(", ", CommandPath.ExtraDirectories)}.",
        _ => $"{config.Name} could not be started. ({e.Message})",
    };

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}

/// <summary>A connected server could not be reached or would not talk. The message is fit to show a person.</summary>
public sealed class McpException(string message, Exception? inner = null) : Exception(message, inner);
