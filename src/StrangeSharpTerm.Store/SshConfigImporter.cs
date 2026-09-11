using System.Collections.Frozen;
using System.Globalization;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Store;

/// <summary>Why part of an ssh_config did not survive the import.</summary>
public abstract record SshConfigWarningReason
{
    private SshConfigWarningReason() { }

    /// <summary><c>Match</c> blocks depend on runtime state that cannot be evaluated.</summary>
    public sealed record MatchBlockSkipped(IReadOnlyList<string> Criteria) : SshConfigWarningReason;

    public sealed record UnsupportedKeyword(string Keyword) : SshConfigWarningReason;

    public sealed record MalformedValue(string Keyword, string Value) : SshConfigWarningReason;
}

public sealed record SshConfigWarning(SshConfigWarningReason Reason, int? LineNumber);

/// <param name="Defaults">
/// Settings from <c>Host *</c> and from any global block, to be attached to the
/// folder that parents every imported connection.
/// </param>
public sealed record SshConfigImportResult(
    ConnectionSettings Defaults,
    IReadOnlyList<Connection> Connections,
    IReadOnlyList<SshConfigWarning> Warnings);

/// <summary>
/// Turns a parsed ssh_config into inventory objects.
///
/// Two rules shape the mapping:
/// <list type="number">
/// <item>A <c>Host</c> block whose patterns are all wildcards (<c>Host *</c>) is not a
/// server, it is a set of defaults. Those become folder-level settings, which is
/// exactly what the inheritance model already expresses.</item>
/// <item>Anything that cannot be represented is reported as a warning rather than
/// dropped silently, because the user's config is the source of truth and they
/// need to know what did not survive the trip.</item>
/// </list>
/// </summary>
public static class SshConfigImporter
{
    /// <summary>
    /// Keywords ssh understands that map onto nothing in the model. Listed
    /// explicitly so genuinely unknown keywords still produce a warning.
    /// </summary>
    private static readonly FrozenSet<string> KnownButUnmapped = new[]
    {
        "include", "addkeystoagent", "controlmaster", "controlpath", "controlpersist",
        "loglevel", "hashknownhosts", "sendenv", "canonicalizehostname",
        "identitiesonly", "pubkeyauthentication", "passwordauthentication", "preferredauthentications",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static SshConfigImportResult Import(SshConfigFile file)
    {
        var defaults = ConnectionSettings.Empty;
        var connections = new List<Connection>();
        var warnings = new List<SshConfigWarning>();

        foreach (var block in file.Blocks)
        {
            switch (block.Header)
            {
                case SshConfigHeader.Match match:
                    warnings.Add(new SshConfigWarning(
                        new SshConfigWarningReason.MatchBlockSkipped(match.Criteria),
                        block.Entries.FirstOrDefault()?.LineNumber));
                    break;

                case SshConfigHeader.Host host:
                    var settings = SettingsFrom(block, warnings);
                    string[] concrete = [.. host.Patterns.Where(pattern => !IsPattern(pattern))];
                    if (concrete.Length == 0)
                    {
                        // Host * and friends: defaults, not servers.
                        defaults = settings.InheritingFrom(defaults);
                        break;
                    }

                    // An explicit HostName applies to every alias on the Host line.
                    var hostName = block.ArgumentsFor("HostName")?.FirstOrDefault();
                    foreach (var alias in concrete)
                    {
                        connections.Add(new Connection
                        {
                            Name = alias,
                            Hostname = hostName ?? alias,
                            Settings = settings,
                            SortIndex = connections.Count,
                            ImportedFromSshConfig = true,
                        });
                    }
                    break;

                default:
                    defaults = SettingsFrom(block, warnings).InheritingFrom(defaults);
                    break;
            }
        }

        return new SshConfigImportResult(defaults, connections, warnings);
    }

    /// <summary>
    /// A ready-to-merge inventory: one folder holding the file's defaults, with every
    /// imported host beneath it.
    /// </summary>
    public static (InventoryTree Tree, IReadOnlyList<SshConfigWarning> Warnings) Inventory(
        SshConfigFile file, string folderName = "SSH Config")
    {
        var result = Import(file);
        var folder = new Folder { Name = folderName, Settings = result.Defaults };
        var tree = new InventoryTree([folder], result.Connections.Select(c => c with { ParentId = folder.Id }));
        return (tree, result.Warnings);
    }

    /// <summary>True when the token is a glob or negation rather than a literal host.</summary>
    internal static bool IsPattern(string token) =>
        token.Contains('*') || token.Contains('?') || token.StartsWith('!');

    private static ConnectionSettings SettingsFrom(SshConfigBlock block, List<SshConfigWarning> warnings)
    {
        var settings = ConnectionSettings.Empty;
        var identityFiles = new List<string>();
        var forwards = new List<PortForward>();
        var environment = new Dictionary<string, string>();

        foreach (var entry in block.Entries)
        {
            var value = entry.Arguments.Count > 0 ? entry.Arguments[0] : null;

            switch (entry.NormalizedKeyword)
            {
                case "hostname":
                    break; // Consumed by the caller, which needs it per alias.

                case "user":
                    settings = settings with { Username = value };
                    break;

                case "port":
                    if (ParseInt(value) is { } port) settings = settings with { Port = port };
                    else warnings.Add(Malformed(entry));
                    break;

                case "identityfile":
                    if (value is not null) identityFiles.Add(ExpandTilde(value));
                    break;

                case "proxyjump":
                    // A chain is comma-separated: ProxyJump bastion1,bastion2.
                    settings = settings with
                    {
                        JumpHosts =
                        [
                            .. entry.Arguments
                                .SelectMany(argument => argument.Split(',', StringSplitOptions.RemoveEmptyEntries))
                                .Where(hop => !hop.Equals("none", StringComparison.OrdinalIgnoreCase)),
                        ],
                    };
                    break;

                case "forwardagent":
                    settings = settings with { ForwardAgent = ParseBool(value) };
                    break;

                case "compression":
                    settings = settings with { Compression = ParseBool(value) };
                    break;

                case "serveraliveinterval":
                    if (ParseInt(value) is { } interval) settings = settings with { KeepAliveInterval = interval };
                    else warnings.Add(Malformed(entry));
                    break;

                case "connecttimeout":
                    if (ParseInt(value) is { } timeout) settings = settings with { ConnectTimeout = timeout };
                    else warnings.Add(Malformed(entry));
                    break;

                case "stricthostkeychecking":
                    HostKeyPolicy? policy = value?.ToLowerInvariant() switch
                    {
                        "yes" => HostKeyPolicy.Strict,
                        "no" or "off" => HostKeyPolicy.AcceptAny,
                        "accept-new" or "ask" => HostKeyPolicy.AcceptNew,
                        _ => null,
                    };
                    if (policy is null) warnings.Add(Malformed(entry));
                    else settings = settings with { HostKeyPolicy = policy };
                    break;

                case "userknownhostsfile":
                    if (value is not null) settings = settings with { KnownHostsFile = ExpandTilde(value) };
                    break;

                case "setenv":
                    foreach (var argument in entry.Arguments)
                    {
                        if (SplitAssignment(argument) is var (name, assigned)) environment[name] = assigned;
                        else warnings.Add(Malformed(entry));
                    }
                    break;

                case "localforward" or "remoteforward":
                    var kind = entry.NormalizedKeyword == "localforward" ? PortForwardKind.Local : PortForwardKind.Remote;
                    if (ParseForward(entry.Arguments, kind) is { } forward) forwards.Add(forward);
                    else warnings.Add(Malformed(entry));
                    break;

                case "dynamicforward":
                    if (value is not null && ParseBindSpec(value) is var (address, bindPort))
                    {
                        forwards.Add(new PortForward
                        {
                            Kind = PortForwardKind.Dynamic,
                            Name = $"SOCKS {bindPort}",
                            BindAddress = address,
                            BindPort = bindPort,
                        });
                    }
                    else
                    {
                        warnings.Add(Malformed(entry));
                    }
                    break;

                default:
                    if (!KnownButUnmapped.Contains(entry.NormalizedKeyword))
                    {
                        warnings.Add(new SshConfigWarning(
                            new SshConfigWarningReason.UnsupportedKeyword(entry.Keyword), entry.LineNumber));
                    }
                    break;
            }
        }

        return settings with
        {
            IdentityFiles = identityFiles.Count > 0 ? identityFiles : null,
            PortForwards = forwards.Count > 0 ? forwards : null,
            Environment = environment.Count > 0 ? environment : null,
        };
    }

    /// <summary>A forward in either form <c>LocalForward</c> accepts: <c>8080 localhost:80</c> or <c>8080:localhost:80</c>.</summary>
    public static PortForward? ParseForward(IReadOnlyList<string> arguments, PortForwardKind kind)
    {
        string bindToken, destinationToken;
        switch (arguments.Count)
        {
            case 2:
                (bindToken, destinationToken) = (arguments[0], arguments[1]);
                break;
            case 1:
                // Colon-joined: split the destination host:port off the right.
                var parts = arguments[0].Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    return null;
                (bindToken, destinationToken) = (string.Join(':', parts[..^2]), string.Join(':', parts[^2..]));
                break;
            default:
                return null;
        }

        if (ParseBindSpec(bindToken) is not var (bindAddress, bindPort)
            || ParseEndpoint(destinationToken) is not var (host, port))
        {
            return null;
        }

        return new PortForward
        {
            Kind = kind,
            Name = $"{host}:{port}",
            BindAddress = bindAddress,
            BindPort = bindPort,
            DestinationHost = host,
            DestinationPort = port,
        };
    }

    /// <summary><c>port</c>, <c>bind:port</c>, or <c>[::1]:port</c>.</summary>
    internal static (string Address, int Port)? ParseBindSpec(string token) =>
        ParseInt(token) is { } port ? ("", port) : ParseEndpoint(token);

    /// <summary><c>host:port</c>, tolerating a bracketed IPv6 literal.</summary>
    internal static (string Host, int Port)? ParseEndpoint(string token)
    {
        if (token.StartsWith('[') && token.IndexOf(']') is var close and > 0)
        {
            var remainder = token[(close + 1)..];
            return remainder.StartsWith(':') && ParseInt(remainder[1..]) is { } bracketedPort
                ? (token[1..close], bracketedPort)
                : null;
        }

        var separator = token.LastIndexOf(':');
        return separator >= 0 && ParseInt(token[(separator + 1)..]) is { } port
            ? (token[..separator], port)
            : null;
    }

    private static SshConfigWarning Malformed(SshConfigEntry entry) =>
        new(new SshConfigWarningReason.MalformedValue(entry.Keyword, string.Join(' ', entry.Arguments)), entry.LineNumber);

    private static bool? ParseBool(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null,
    };

    // Swift's Int(String): an optional sign and digits, nothing else, no whitespace.
    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// <c>NAME=value</c>, split as Swift's <c>split(separator: "=", maxSplits: 1)</c>
    /// did: empty pieces are dropped, so leading '='s are ignored and an empty name
    /// or value makes the pair malformed, while the value may itself contain '='.
    /// </summary>
    private static (string Name, string Value)? SplitAssignment(string argument)
    {
        var text = argument.TrimStart('=');
        var separator = text.IndexOf('=');
        return separator > 0 && separator < text.Length - 1 ? (text[..separator], text[(separator + 1)..]) : null;
    }

    /// <summary>
    /// <c>~</c> and <c>~/…</c> become the home directory. The Swift app also resolved
    /// <c>~user/…</c> through the user database, which has no portable equivalent;
    /// those are left as written.
    /// </summary>
    private static string ExpandTilde(string path) =>
        path == "~" || (path.Length > 1 && path[0] == '~' && path[1] is '/' or '\\')
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;
}
