// stctl: the headless driver.
//
// It exists so the integration test can drive the transport layer without a UI,
// and so a person can check one host from a terminal. Every command opens its own
// process, which is why the multiplexing assertions live inside a single
// invocation: with no background agent, a connection lives exactly as long as the
// process that opened it.

using System.CommandLine;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;
using StrangeSharpTerm.Transport;

var target = new Argument<string>("target") { Description = "[user@]host[:port]" };

var keyOption = new Option<string?>("--key") { Description = "Private key file to authenticate with." };
var knownHostsOption = new Option<string?>("--known-hosts") { Description = "known_hosts file to check against and record in." };
var insecureOption = new Option<bool>("--insecure") { Description = "Accept and record an unknown host key without asking. For throwaway servers only." };
var timeoutOption = new Option<int>("--timeout") { Description = "Connect timeout in seconds.", DefaultValueFactory = _ => 15 };

foreach (var option in new Option[] { keyOption, knownHostsOption, insecureOption, timeoutOption })
    option.Recursive = true;

var root = new RootCommand("Drive StrangeSharpTerm's transport layer from a terminal.");
root.Add(keyOption);
root.Add(knownHostsOption);
root.Add(insecureOption);
root.Add(timeoutOption);

ResolvedConnection Resolve(ParseResult parsed)
{
    var (user, host, port) = SshSessionFactory.ParseEndpoint(parsed.GetValue(target)!, new ResolvedSettings(ConnectionSettings.Empty));
    var key = parsed.GetValue(keyOption);

    var connection = new Connection
    {
        Name = host,
        Hostname = host,
        Settings = new ConnectionSettings
        {
            Username = user,
            Port = port,
            ConnectTimeout = parsed.GetValue(timeoutOption),
            KnownHostsFile = parsed.GetValue(knownHostsOption),
            // Unknown keys are refused unless --insecure says otherwise, and a
            // changed key is refused either way.
            HostKeyPolicy = HostKeyPolicy.AcceptNew,
            IdentityFiles = key is null ? null : [key],
        },
    };
    return new InventoryTree(connections: [connection]).Resolve(connection.Id);
}

SshNetSession Open(ParseResult parsed)
{
    var factory = new SshSessionFactory(
        new InMemorySecretStore(),
        parsed.GetValue(insecureOption) ? new TrustUnknownHostKeys() : new RefuseUnknownHostKeys());
    return (SshNetSession)factory.Connect(Resolve(parsed));
}

/// Runs an action against a session, turning any failure into a summary a person
/// can act on and a non-zero exit code.
int WithSession(ParseResult parsed, Func<SshNetSession, int> action)
{
    try
    {
        using var session = Open(parsed);
        return action(session);
    }
    catch (Exception e)
    {
        var failure = SshFailure.Classify(e);
        Console.Error.WriteLine(failure.Summary);
        return 1;
    }
}

// ------------------------------------------------------------------ exec
var repeatOption = new Option<int>("--repeat") { Description = "Run the command this many times on one connection.", DefaultValueFactory = _ => 1 };
var commandArgument = new Argument<string[]>("command") { Description = "Command and arguments, after --.", Arity = ArgumentArity.OneOrMore };

var exec = new Command("exec", "Run a command on the host.") { target, repeatOption, commandArgument };
exec.SetAction(parsed => WithSession(parsed, session =>
{
    var text = string.Join(' ', parsed.GetValue(commandArgument)!);
    var status = 0;
    for (var i = 0; i < parsed.GetValue(repeatOption); i++)
    {
        var result = session.Run(text);
        Console.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
        status = result.ExitStatus;
    }
    return status;
}));
root.Add(exec);

// ----------------------------------------------------------------- probe
var probe = new Command("probe", "Collect the dashboard metrics from the host.") { target };
probe.SetAction(parsed => WithSession(parsed, session =>
{
    var metrics = ServerProbe.Parse(session.Run(ServerProbe.Command).StandardOutput);
    Console.WriteLine($"uptime={metrics.Uptime}");
    Console.WriteLine($"load={(metrics.LoadAverages is { } load ? string.Join(' ', load) : "")}");
    Console.WriteLine($"memory={metrics.MemoryUsedBytes}/{metrics.MemoryTotalBytes}");
    Console.WriteLine($"disk={metrics.DiskUsedBytes}/{metrics.DiskTotalBytes}");
    foreach (var container in metrics.Containers)
        Console.WriteLine($"container={container.Name} running={container.IsRunning}");
    return metrics.IsEmpty ? 1 : 0;
}));
root.Add(probe);

// --------------------------------------------------------------- forward
var forwardOption = new Option<string>("-L") { Description = "[bind:]port:host:port", Required = true };
var checkOption = new Option<bool>("--check") { Description = "Prove the forward carries traffic, then stop it and prove the port is released." };

var forward = new Command("forward", "Open a local port forward.") { target, forwardOption, checkOption };
forward.SetAction(async (parsed, cancellationToken) =>
{
    var specification = SshConfigForward.Parse(parsed.GetValue(forwardOption)!);
    if (specification is null)
    {
        Console.Error.WriteLine("A forward is [bind:]port:host:port.");
        return 1;
    }

    try
    {
        using var session = Open(parsed);
        var (bind, boundPort, host, port) = specification.Value;
        var running = session.StartLocalForward(bind, (uint)boundPort, host, (uint)port);
        Console.WriteLine($"listening={boundPort}");

        if (!parsed.GetValue(checkOption))
        {
            // Nothing keeps a tunnel alive but this process: no agent outlives it.
            Console.WriteLine("holding; press Ctrl+C to stop");
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Asked to stop, which is the ordinary way out of holding.
            }
            return 0;
        }

        var banner = ReadBanner(boundPort);
        Console.WriteLine($"banner={banner}");
        running.Stop();
        var released = !await LocalPortProbe.IsListeningAsync(boundPort, cancellationToken: cancellationToken);
        Console.WriteLine($"released={(released ? "yes" : "no")}");
        return banner.StartsWith('<') || !released ? 1 : 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine(SshFailure.Classify(e).Summary);
        return 1;
    }
});
root.Add(forward);

// ------------------------------------------------------------------ sftp
var localArgument = new Argument<string>("local");
var remoteArgument = new Argument<string>("remote");

var put = new Command("put", "Upload a file.") { target, localArgument, remoteArgument };
put.SetAction(parsed => WithSession(parsed, session =>
{
    using var stream = File.OpenRead(parsed.GetValue(localArgument)!);
    session.OpenSftp().UploadFile(stream, parsed.GetValue(remoteArgument)!);
    Console.WriteLine($"uploaded={Sha256(parsed.GetValue(localArgument)!)}");
    return 0;
}));

var get = new Command("get", "Download a file.") { target, remoteArgument, localArgument };
get.SetAction(parsed => WithSession(parsed, session =>
{
    var destination = parsed.GetValue(localArgument)!;
    using (var stream = File.Create(destination))
        session.OpenSftp().DownloadFile(parsed.GetValue(remoteArgument)!, stream);
    Console.WriteLine($"downloaded={Sha256(destination)}");
    return 0;
}));

var sftp = new Command("sftp", "Move files over SFTP.") { put, get };
root.Add(sftp);

return root.Parse(args).Invoke();

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

/// Reads the first line a server sends, which for sshd is its version banner.
static string ReadBanner(int port)
{
    try
    {
        using var probe = new TcpClient();
        probe.Connect("127.0.0.1", port);
        probe.ReceiveTimeout = 5000;
        var buffer = new byte[64];
        var read = probe.GetStream().Read(buffer, 0, buffer.Length);
        return Encoding.ASCII.GetString(buffer, 0, read).Trim();
    }
    catch (Exception e)
    {
        return $"<{e.GetType().Name}>";
    }
}

/// <summary>ssh's own <c>-L</c> syntax, which is what people already know.</summary>
internal static class SshConfigForward
{
    public static (string Bind, int BoundPort, string Host, int Port)? Parse(string specification)
    {
        var parts = specification.Split(':');
        return parts.Length switch
        {
            3 when int.TryParse(parts[0], out var bound) && int.TryParse(parts[2], out var port) =>
                ("127.0.0.1", bound, parts[1], port),
            4 when int.TryParse(parts[1], out var bound) && int.TryParse(parts[3], out var port) =>
                (parts[0], bound, parts[2], port),
            _ => null,
        };
    }
}
