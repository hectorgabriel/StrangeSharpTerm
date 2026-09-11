using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshNet.Agent;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Transport;

/// <summary>What a server offered, for a person to judge.</summary>
public sealed record HostKeyOffer(
    string Host, int Port, string KeyType, string KeyBase64, string Fingerprint, HostKeyVerdict Verdict);

/// <summary>Asked when the policy leaves an unknown key to a person.</summary>
public interface IHostKeyPrompt
{
    bool ShouldTrust(HostKeyOffer offer);
}

/// <summary>Refuses every unknown key: the safe default where no one can be asked.</summary>
public sealed class RefuseUnknownHostKeys : IHostKeyPrompt
{
    public bool ShouldTrust(HostKeyOffer offer) => false;
}

/// <summary>Trusts any unknown key. For loopback tests against a throwaway server, and nothing else.</summary>
public sealed class TrustUnknownHostKeys : IHostKeyPrompt
{
    public bool ShouldTrust(HostKeyOffer offer) => true;
}

/// <summary>
/// Opens authenticated sessions: host key verification, credentials, and jump
/// hosts. Hand its <see cref="Connect"/> to a <see cref="ConnectionPool"/> and the
/// pool does the rest.
/// </summary>
public sealed class SshSessionFactory(
    ISecretStore secrets,
    IHostKeyPrompt? prompt = null,
    Func<NodeId, Credential?>? credentials = null)
{
    public const int DefaultPort = 22;

    private readonly IHostKeyPrompt _prompt = prompt ?? new RefuseUnknownHostKeys();

    public ISshSession Connect(ResolvedConnection resolved)
    {
        var settings = resolved.Settings;
        var credential = settings.CredentialId is { } id ? credentials?.Invoke(id) : null;
        var hops = new List<SshNetSession>();

        try
        {
            // The chain is nearest hop first, each opened through the one before it.
            foreach (var jump in settings.JumpHosts)
            {
                var hop = ParseEndpoint(jump, settings);
                hops.Add(Open(hop.Host, hop.Port, hop.User ?? Username(settings), credential, settings, hops.LastOrDefault()));
            }

            var session = Open(
                resolved.Connection.Hostname, settings.Port ?? DefaultPort, Username(settings),
                credential, settings, hops.LastOrDefault());

            foreach (var hop in hops)
                session.Carry(hop);
            return session;
        }
        catch
        {
            for (var i = hops.Count - 1; i >= 0; i--)
                hops[i].Dispose();
            throw;
        }
    }

    private SshNetSession Open(
        string host, int port, string user, Credential? credential, ResolvedSettings settings, SshNetSession? through)
    {
        // Through a jump host, the connection is made to a local forward — but the
        // key is still checked against the real host's name, which is the only
        // name that means anything in known_hosts.
        var endpoint = (Host: host, Port: port);
        ForwardedPortLocal? forward = null;
        if (through is not null)
        {
            var local = FreeLoopbackPort();
            forward = through.StartLocalForward("127.0.0.1", (uint)local, host, (uint)port);
            endpoint = ("127.0.0.1", local);
        }

        var connectionInfo = new ConnectionInfo(
            endpoint.Host, endpoint.Port, user, AuthenticationMethods(user, credential, settings))
        {
            Timeout = TimeSpan.FromSeconds(settings.ConnectTimeout),
        };

        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(settings.KeepAliveInterval),
        };

        var knownHostsPath = settings.KnownHostsFile ?? KnownHostsWriter.DefaultPath();
        var knownHosts = KnownHostsFile.Read(knownHostsPath);
        string? fingerprint = null;
        HostKeyVerdict? refused = null;

        client.HostKeyReceived += (_, e) =>
        {
            var keyBase64 = Convert.ToBase64String(e.HostKey);
            fingerprint = HostKeyFingerprint.Sha256(keyBase64);
            var verdict = knownHosts.Verdict(host, port, e.HostKeyName, keyBase64);

            e.CanTrust = HostKeyPolicyRules.Decide(settings.HostKeyPolicy, verdict) switch
            {
                HostKeyDecision.Accept => true,
                HostKeyDecision.Prompt => _prompt.ShouldTrust(
                    new HostKeyOffer(host, port, e.HostKeyName, keyBase64, fingerprint, verdict)),
                _ => false,
            };

            if (!e.CanTrust)
            {
                refused = verdict;
                return;
            }
            // Trust goes into OpenSSH's own file, so a host trusted here is
            // trusted by ssh on the command line too.
            if (verdict is HostKeyVerdict.Unknown)
                new KnownHostsWriter(knownHostsPath).Append(host, port, e.HostKeyName, keyBase64);
        };

        try
        {
            client.Connect();
        }
        catch
        {
            client.Dispose();
            forward?.Stop();
            if (refused is { } verdict)
            {
                throw new HostKeyRejectedException(verdict, verdict switch
                {
                    HostKeyVerdict.Changed changed =>
                        $"{host} offered a host key that does not match the one stored ({changed.StoredFingerprint}).",
                    HostKeyVerdict.Revoked => $"{host} offered a host key marked as revoked.",
                    _ => $"{host} is not a known host, and its key was not accepted.",
                });
            }
            throw;
        }

        return new SshNetSession(client, connectionInfo, fingerprint);
    }

    private AuthenticationMethod[] AuthenticationMethods(string user, Credential? credential, ResolvedSettings settings)
    {
        var methods = new List<AuthenticationMethod>();

        switch (credential?.Method)
        {
            case CredentialMethod.Password when secrets.Secret(credential.SecretAccount) is { } password:
                methods.Add(new PasswordAuthenticationMethod(user, password));
                break;

            case CredentialMethod.IdentityFile when credential.IdentityFile is { } path:
                methods.Add(new PrivateKeyAuthenticationMethod(user, LoadKey(path, credential.SecretAccount)));
                break;

            default:
                // The preferred method: the key never enters this process.
                if (AgentIdentities() is { Length: > 0 } identities)
                    methods.Add(new PrivateKeyAuthenticationMethod(user, identities));
                break;
        }

        // Identity files named by ssh_config apply whatever the credential says,
        // as they do for ssh itself.
        var keys = settings.IdentityFiles
            .Select(path => TryLoadKey(path, $"identity:{path}"))
            .OfType<IPrivateKeySource>()
            .ToArray();
        if (keys.Length > 0)
            methods.Add(new PrivateKeyAuthenticationMethod(user, keys));

        if (methods.Count == 0)
            methods.Add(new NoneAuthenticationMethod(user));
        return [.. methods];
    }

    private IPrivateKeySource[] AgentIdentities()
    {
        try
        {
            var identities = new SshAgent(TimeSpan.FromSeconds(5)).RequestIdentities();
            if (identities.Length > 0)
                return identities;
        }
        catch (Exception)
        {
            // No agent, or none reachable. Fall through to Pageant on Windows.
        }

        if (!OperatingSystem.IsWindows())
            return [];

        try
        {
            return new Pageant(TimeSpan.FromSeconds(5)).RequestIdentities();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private PrivateKeyFile LoadKey(string path, string secretAccount) =>
        secrets.Secret(secretAccount) is { } passphrase
            ? new PrivateKeyFile(ExpandHome(path), passphrase)
            : new PrivateKeyFile(ExpandHome(path));

    private IPrivateKeySource? TryLoadKey(string path, string secretAccount)
    {
        try
        {
            return LoadKey(path, secretAccount);
        }
        catch (Exception)
        {
            // An unreadable or passphrase-protected key is not a reason to refuse
            // to try the others; authentication reports what actually failed.
            return null;
        }
    }

    private static string Username(ResolvedSettings settings) => settings.Username ?? Environment.UserName;

    /// <summary><c>host</c>, <c>user@host</c>, or either with <c>:port</c>.</summary>
    public static (string? User, string Host, int Port) ParseEndpoint(string specification, ResolvedSettings settings)
    {
        var text = specification;
        string? user = null;

        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            user = text[..at];
            text = text[(at + 1)..];
        }

        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port))
            return (user, text[..colon], port);

        return (user, text, settings.Port ?? DefaultPort);
    }

    /// <summary>
    /// A loopback port nothing is using, taken by binding and releasing one. The
    /// alternative is asking the forward to pick, which gives no way to learn
    /// which port it chose until after something needs it.
    /// </summary>
    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;
}
