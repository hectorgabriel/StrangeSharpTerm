using System.Text.Json.Serialization;

namespace StrangeSharpTerm.Model;

[JsonConverter(typeof(JsonStringEnumConverter<CredentialMethod>))]
public enum CredentialMethod
{
    /// <summary>
    /// Delegate to a running ssh-agent. Preferred: key material never enters our
    /// address space at all.
    /// </summary>
    [JsonStringEnumMemberName("agent")] Agent,

    /// <summary>A private key on disk, with its passphrase in the secret store if it has one.</summary>
    [JsonStringEnumMemberName("identityFile")] IdentityFile,

    /// <summary>A password in the secret store.</summary>
    [JsonStringEnumMemberName("password")] Password,
}

public static class CredentialMethodExtensions
{
    public static string Label(this CredentialMethod method) => method switch
    {
        CredentialMethod.Agent => "ssh-agent",
        CredentialMethod.IdentityFile => "Key file",
        _ => "Password",
    };
}

/// <summary>
/// A reusable way of authenticating, shared by any number of hosts.
///
/// A key or password is described once and then referred to, rather than
/// repeated on every host that uses it. Because a connection's credential is an
/// inherited setting, putting one on a folder covers every host beneath it, and a
/// single host can still override.
///
/// The secret itself is never here. This lives in the inventory, an ordinary
/// JSON file; only the secret store account is derived from it.
/// </summary>
public sealed record Credential
{
    [JsonPropertyName("id"), JsonRequired]
    public NodeId Id { get; init; } = NodeId.New();

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Applied to hosts that do not name one themselves.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("method"), JsonRequired]
    public CredentialMethod Method { get; init; } = CredentialMethod.Agent;

    /// <summary>Path to the private key, for <see cref="CredentialMethod.IdentityFile"/>.</summary>
    [JsonPropertyName("identityFile")]
    public string? IdentityFile { get; init; }

    [JsonPropertyName("sortIndex"), JsonRequired]
    public int SortIndex { get; init; }

    /// <summary>
    /// Where the secret lives. Keyed by id rather than name, so renaming a
    /// credential does not orphan its password.
    /// </summary>
    [JsonIgnore]
    public string SecretAccount => $"credential-{Id}";

    /// <summary>
    /// Whether this method needs a stored secret at all. An agent credential never
    /// does, and a key file only when the key is encrypted, which is why an empty
    /// passphrase is a legitimate state rather than an incomplete one.
    /// </summary>
    [JsonIgnore]
    public bool CanHaveSecret => Method != CredentialMethod.Agent;

    /// <summary>
    /// Applies this credential to settings that have not specified their own. A
    /// host's own value always wins: a shared credential is a default for the
    /// hosts that use it, not an override of the ones that were explicit.
    /// </summary>
    public ConnectionSettings AppliedTo(ConnectionSettings settings) => settings with
    {
        Username = settings.Username ?? Username,
        IdentityFiles = settings.IdentityFiles
            ?? (Method == CredentialMethod.IdentityFile && IdentityFile is not null ? [IdentityFile] : null),
    };
}
