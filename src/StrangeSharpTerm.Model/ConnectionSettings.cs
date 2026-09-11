using System.Text.Json.Serialization;

namespace StrangeSharpTerm.Model;

/// <summary>How aggressively to verify the server's host key.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostKeyPolicy>))]
public enum HostKeyPolicy
{
    /// <summary>Refuse anything not already in known_hosts. The safest setting.</summary>
    [JsonStringEnumMemberName("strict")] Strict,

    /// <summary>
    /// Trust on first use: prompt with the fingerprint for an unknown host, but
    /// still hard-fail a <em>changed</em> key. This is the product default.
    /// </summary>
    [JsonStringEnumMemberName("acceptNew")] AcceptNew,

    /// <summary>
    /// Accept unknown keys silently. Never a default; only reachable by explicit
    /// opt-in, and the UI is expected to mark hosts using it.
    /// </summary>
    [JsonStringEnumMemberName("acceptAny")] AcceptAny,
}

/// <summary>
/// A <em>partial</em> set of connection settings.
///
/// Every field is optional, and null means "not specified here, ask my parent".
/// Folders and connections both carry one of these; a connection's effective
/// configuration is produced by walking from the root of the tree down to the
/// node and folding each level in turn (see <see cref="InventoryTree.Resolve"/>).
///
/// Merge semantics differ by field and are deliberately explicit, because
/// "child wins" is wrong for some of them:
/// <list type="bullet">
/// <item><b>Scalars and lists</b>: the child's value replaces the parent's outright.</item>
/// <item><b><see cref="Environment"/></b>: merged key by key, with the child winning per
/// key, so a folder can set LANG for every host while one host adds AWS_PROFILE.</item>
/// <item><b><see cref="PortForwards"/></b>: accumulated. A folder-level forward applies
/// to every host beneath it, and hosts add their own rather than replacing.</item>
/// </list>
/// </summary>
public sealed record ConnectionSettings
{
    public static ConnectionSettings Empty { get; } = new();

    // Scalars: child replaces parent.

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("port")]
    public int? Port { get; init; }

    [JsonPropertyName("connectTimeout")]
    public int? ConnectTimeout { get; init; }

    [JsonPropertyName("keepAliveInterval")]
    public int? KeepAliveInterval { get; init; }

    [JsonPropertyName("compression")]
    public bool? Compression { get; init; }

    [JsonPropertyName("forwardAgent")]
    public bool? ForwardAgent { get; init; }

    [JsonPropertyName("hostKeyPolicy")]
    public HostKeyPolicy? HostKeyPolicy { get; init; }

    /// <summary>
    /// Overrides <c>~/.ssh/known_hosts</c>. Lets a folder keep its own trust store,
    /// and keeps throwaway hosts out of the user's real one.
    /// </summary>
    [JsonPropertyName("knownHostsFile")]
    public string? KnownHostsFile { get; init; }

    /// <summary>
    /// A shared credential from the library. Inherited like any other scalar, so
    /// setting one on a folder covers every host beneath it.
    /// </summary>
    [JsonPropertyName("credentialID")]
    public NodeId? CredentialId { get; init; }

    [JsonPropertyName("terminalTheme")]
    public string? TerminalTheme { get; init; }

    [JsonPropertyName("terminalFontSize")]
    public double? TerminalFontSize { get; init; }

    // Lists: child replaces parent when present.

    [JsonPropertyName("identityFiles")]
    public IReadOnlyList<string>? IdentityFiles { get; init; }

    /// <summary>ProxyJump chain, nearest hop first.</summary>
    [JsonPropertyName("jumpHosts")]
    public IReadOnlyList<string>? JumpHosts { get; init; }

    // Dictionary: merged key-wise.

    [JsonPropertyName("environment")]
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    // Accumulated across the whole chain.

    [JsonPropertyName("portForwards")]
    public IReadOnlyList<PortForward>? PortForwards { get; init; }

    /// <summary>
    /// Folds this layer on top of an ancestor's settings, applying the per-field
    /// semantics documented on the type. <c>this</c> is the more specific (child) layer.
    /// </summary>
    public ConnectionSettings InheritingFrom(ConnectionSettings parent)
    {
        IReadOnlyDictionary<string, string>? environment = parent.Environment;
        if (Environment is not null)
        {
            // Key-wise merge; the child's binding for a given key wins.
            var merged = parent.Environment is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(parent.Environment);
            foreach (var (key, value) in Environment)
                merged[key] = value;
            environment = merged;
        }

        // Accumulate, keeping ancestor forwards first. Dedupe by id so that
        // re-resolving an already merged set stays idempotent.
        PortForward[] forwards = [.. (parent.PortForwards ?? []).Concat(PortForwards ?? []).DistinctBy(f => f.Id)];

        return new ConnectionSettings
        {
            Username = Username ?? parent.Username,
            Port = Port ?? parent.Port,
            ConnectTimeout = ConnectTimeout ?? parent.ConnectTimeout,
            KeepAliveInterval = KeepAliveInterval ?? parent.KeepAliveInterval,
            Compression = Compression ?? parent.Compression,
            ForwardAgent = ForwardAgent ?? parent.ForwardAgent,
            HostKeyPolicy = HostKeyPolicy ?? parent.HostKeyPolicy,
            KnownHostsFile = KnownHostsFile ?? parent.KnownHostsFile,
            CredentialId = CredentialId ?? parent.CredentialId,
            TerminalTheme = TerminalTheme ?? parent.TerminalTheme,
            TerminalFontSize = TerminalFontSize ?? parent.TerminalFontSize,
            IdentityFiles = IdentityFiles ?? parent.IdentityFiles,
            JumpHosts = JumpHosts ?? parent.JumpHosts,
            Environment = environment,
            PortForwards = forwards.Length == 0 ? null : forwards,
        };
    }
}

/// <summary>
/// Connection settings with every fallback applied, nothing optional left to
/// decide, so the transport never has to invent a default halfway through
/// opening a connection.
/// </summary>
public sealed record ResolvedSettings
{
    public const int DefaultConnectTimeout = 15;
    public const int DefaultKeepAliveInterval = 30;
    public const string DefaultTerminalTheme = "StrangeTerm Dark";
    public const double DefaultTerminalFontSize = 13;

    /// <summary>
    /// <see cref="Username"/> and <see cref="Port"/> intentionally stay optional
    /// after resolution. The Swift app left them unset so <c>~/.ssh/config</c> kept
    /// control; with ssh now in-process, choosing the fallback is the transport's
    /// job, and it must still be able to tell "unset" from "set".
    /// </summary>
    public ResolvedSettings(ConnectionSettings partial)
    {
        Username = partial.Username;
        Port = partial.Port;
        ConnectTimeout = partial.ConnectTimeout ?? DefaultConnectTimeout;
        KeepAliveInterval = partial.KeepAliveInterval ?? DefaultKeepAliveInterval;
        Compression = partial.Compression ?? false;
        ForwardAgent = partial.ForwardAgent ?? false;
        HostKeyPolicy = partial.HostKeyPolicy ?? Model.HostKeyPolicy.AcceptNew;
        KnownHostsFile = partial.KnownHostsFile;
        CredentialId = partial.CredentialId;
        TerminalTheme = partial.TerminalTheme ?? DefaultTerminalTheme;
        TerminalFontSize = partial.TerminalFontSize ?? DefaultTerminalFontSize;
        IdentityFiles = partial.IdentityFiles ?? [];
        JumpHosts = partial.JumpHosts ?? [];
        Environment = partial.Environment ?? new Dictionary<string, string>();
        PortForwards = partial.PortForwards ?? [];
    }

    public string? Username { get; init; }
    public int? Port { get; init; }
    public int ConnectTimeout { get; init; }
    public int KeepAliveInterval { get; init; }
    public bool Compression { get; init; }
    public bool ForwardAgent { get; init; }
    public HostKeyPolicy HostKeyPolicy { get; init; }

    /// <summary>Null means the default, <c>~/.ssh/known_hosts</c>.</summary>
    public string? KnownHostsFile { get; init; }

    public NodeId? CredentialId { get; init; }
    public string TerminalTheme { get; init; }
    public double TerminalFontSize { get; init; }
    public IReadOnlyList<string> IdentityFiles { get; init; }
    public IReadOnlyList<string> JumpHosts { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; }
    public IReadOnlyList<PortForward> PortForwards { get; init; }
}
