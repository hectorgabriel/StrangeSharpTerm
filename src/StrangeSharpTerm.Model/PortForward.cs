using System.Text.Json.Serialization;

namespace StrangeSharpTerm.Model;

[JsonConverter(typeof(JsonStringEnumConverter<PortForwardKind>))]
public enum PortForwardKind
{
    /// <summary><c>-L</c>: listen locally, deliver through the server.</summary>
    [JsonStringEnumMemberName("local")] Local,

    /// <summary><c>-R</c>: listen on the server, deliver back through us.</summary>
    [JsonStringEnumMemberName("remote")] Remote,

    /// <summary><c>-D</c>: a local SOCKS proxy.</summary>
    [JsonStringEnumMemberName("dynamic")] Dynamic,
}

/// <summary>A single tunnel definition.</summary>
public sealed record PortForward
{
    [JsonPropertyName("id"), JsonRequired]
    public NodeId Id { get; init; } = NodeId.New();

    [JsonPropertyName("kind")]
    public required PortForwardKind Kind { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Empty means loopback only, which is the safe default: binding a forward to
    /// 0.0.0.0 exposes it to the whole network and is always an explicit choice.
    /// </summary>
    [JsonPropertyName("bindAddress"), JsonRequired]
    public string BindAddress { get; init; } = "";

    [JsonPropertyName("bindPort")]
    public required int BindPort { get; init; }

    /// <summary>Unused for <see cref="PortForwardKind.Dynamic"/>, which has no fixed destination.</summary>
    [JsonPropertyName("destinationHost"), JsonRequired]
    public string DestinationHost { get; init; } = "";

    [JsonPropertyName("destinationPort"), JsonRequired]
    public int DestinationPort { get; init; }

    [JsonPropertyName("autoStart"), JsonRequired]
    public bool AutoStart { get; init; }

    /// <summary>
    /// Ports below 1024 need elevated rights to bind. There is no privileged
    /// helper, so the UI must catch this before the user waits on a doomed
    /// connection. Remote forwards bind on the server, where our rights are moot.
    /// </summary>
    [JsonIgnore]
    public bool RequiresPrivilegedBind =>
        Kind is PortForwardKind.Local or PortForwardKind.Dynamic && BindPort < 1024;

    /// <summary>
    /// The forward in ssh's own <c>-L</c>/<c>-R</c>/<c>-D</c> syntax. No ssh binary
    /// is spawned any more; the syntax survives because it is what people type
    /// into the editor, what ssh_config uses, and what the importer parses.
    /// </summary>
    [JsonIgnore]
    public string SshSpecification
    {
        get
        {
            var bind = BindAddress.Length == 0 ? "" : $"{BindAddress}:";
            return Kind == PortForwardKind.Dynamic
                ? $"{bind}{BindPort}"
                : $"{bind}{BindPort}:{DestinationHost}:{DestinationPort}";
        }
    }

    /// <summary>The ssh flag this forward's specification belongs after.</summary>
    [JsonIgnore]
    public string SshFlag => Kind switch
    {
        PortForwardKind.Local => "-L",
        PortForwardKind.Remote => "-R",
        _ => "-D",
    };
}
