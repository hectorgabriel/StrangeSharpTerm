using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Mcp;

/// <summary>
/// How a server is reached.
///
/// The two differ in what they expose, not merely in mechanism, and the UI says
/// so: a local server runs on this machine as you, with your files; an HTTP one
/// receives whatever its tools are given, over the network, at an address you
/// can read.
/// </summary>
public enum McpTransport
{
    /// <summary>Launched as a child process, spoken to over its stdin and stdout.</summary>
    Local,

    /// <summary>Streamable HTTP against a URL.</summary>
    Http,
}

/// <summary>
/// One connected tool server, as the settings file keeps it.
///
/// No secret lives here. An HTTP bearer token and any OAuth tokens go to the
/// platform store; this type has nowhere to put one, which is the same
/// structural boundary <c>HostContext</c> draws and for the same reason.
/// </summary>
public sealed record McpServerConfig
{
    public NodeId Id { get; init; } = NodeId.New();

    /// <summary>What you called it. Also the prefix its tools are namespaced with.</summary>
    public required string Name { get; init; }

    public McpTransport Transport { get; init; } = McpTransport.Local;

    /// <summary>The command, for a local server. Bare names are looked up — see <see cref="CommandPath"/>.</summary>
    public string Command { get; init; } = "";

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Extra environment for the child. Not a place for secrets; the store is.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The endpoint, for an HTTP server.</summary>
    public string Url { get; init; } = "";

    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Tools this server may call without asking, by their bare name.
    ///
    /// The only way onto this list is a person pressing Always allow at the
    /// gate. Nothing a server says about its own tools puts one here.
    /// </summary>
    public IReadOnlyList<string> AlwaysAllowed { get; init; } = [];

    /// <summary>Where the account name for this server's secrets comes from.</summary>
    public string SecretAccount => $"mcp.{Id.Value}";

    /// <summary>What the row under the name shows: the command, or the address.</summary>
    public string Where => Transport == McpTransport.Http
        ? Url
        : string.Join(' ', new[] { Command }.Concat(Arguments)).Trim();

    /// <summary>
    /// Why this cannot be connected, or null.
    ///
    /// Said before an attempt rather than after one fails, which is the same
    /// thing the tunnels pane does about a privileged port.
    /// </summary>
    public string? Problem =>
        Name.Trim().Length == 0 ? "It needs a name."
        : Transport == McpTransport.Http
            ? Uri.TryCreate(Url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                    ? null
                    : "It needs an http or https address."
            : Command.Trim().Length == 0 ? "It needs a command to run."
            : null;

    public bool IsUsable => Problem is null;
}
