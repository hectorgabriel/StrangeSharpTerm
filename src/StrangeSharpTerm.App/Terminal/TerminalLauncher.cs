using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Terminal;

/// <summary>
/// Command-line arguments for opening a terminal on one host.
///
/// A stand-in for the inventory and the sidebar, which arrive with the app shell
/// in M4. It exists so this milestone can be run rather than only reviewed.
/// </summary>
public sealed record TerminalLaunchRequest(string Target, string? KeyFile, bool TrustUnknownHostKey, string? Theme)
{
    /// <summary>
    /// <c>--connect user@host[:port] [--key path] [--insecure] [--theme name]</c>,
    /// or null when nothing was asked for.
    /// </summary>
    public static TerminalLaunchRequest? Parse(string[] arguments)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }

        return Value("--connect") is { } target
            ? new TerminalLaunchRequest(target, Value("--key"), arguments.Contains("--insecure"), Value("--theme"))
            : null;
    }
}

public static class TerminalLauncher
{
    /// <summary>
    /// Opens a shell on a host from the inventory, with its folder inheritance
    /// and credential applied.
    ///
    /// Unknown host keys are refused rather than accepted: the sheet that shows a
    /// fingerprint and asks is still to come, and silently trusting a first
    /// contact is the one thing that must not happen while it is missing.
    /// </summary>
    public static TerminalSession Connect(InventoryTree tree, Connection connection, IHostKeyPrompt? prompt = null)
    {
        var factory = new SshSessionFactory(
            new PlatformSecretStore(),
            prompt ?? new RefuseUnknownHostKeys(),
            id => tree.Credentials.GetValueOrDefault(id));

        var session = (SshNetSession)factory.Connect(tree.Resolve(connection.Id));
        return new TerminalSession(SshTerminalChannel.Open(session));
    }

    /// <summary>Opens a session on the host named by the request, ready to attach to a view.</summary>
    public static TerminalSession Connect(TerminalLaunchRequest request)
    {
        var (user, host, port) = SshSessionFactory.ParseEndpoint(
            request.Target, new ResolvedSettings(ConnectionSettings.Empty));

        var connection = new Connection
        {
            Name = host,
            Hostname = host,
            Settings = new ConnectionSettings
            {
                Username = user,
                Port = port,
                IdentityFiles = request.KeyFile is null ? null : [request.KeyFile],
                // A changed key is refused whatever this says; --insecure only
                // decides what happens on first contact.
                HostKeyPolicy = HostKeyPolicy.AcceptNew,
            },
        };

        var factory = new SshSessionFactory(
            new PlatformSecretStore(),
            request.TrustUnknownHostKey ? new TrustUnknownHostKeys() : new RefuseUnknownHostKeys());

        var resolved = new InventoryTree(connections: [connection]).Resolve(connection.Id);
        var session = (SshNetSession)factory.Connect(resolved);
        return new TerminalSession(SshTerminalChannel.Open(session));
    }
}
