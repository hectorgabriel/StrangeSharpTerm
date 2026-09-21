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
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Mcp;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;
using StrangeSharpTerm.Terminal;
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

// Written to the command's standard input, which is then closed: the path a
// host's password takes to sudo. Here so the integration gate can prove against
// a real server that the input arrives and that the channel's input really is
// closed after it -- if it were not, the command would wait forever for more.
var inputOption = new Option<string?>("--input") { Description = "Write this to the command's standard input, then close it." };

var exec = new Command("exec", "Run a command on the host.") { target, repeatOption, inputOption, commandArgument };
exec.SetAction(parsed => WithSession(parsed, session =>
{
    var text = string.Join(' ', parsed.GetValue(commandArgument)!);
    var input = parsed.GetValue(inputOption);
    var status = 0;
    for (var i = 0; i < parsed.GetValue(repeatOption); i++)
    {
        var result = input is null
            ? session.Run(text)
            : session.RunFeeding(text, TimeSpan.FromSeconds(parsed.GetValue(timeoutOption)), input);
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


// -------------------------------------------------------------- terminal
var runOption = new Option<string?>("--run") { Description = "Type this line into the shell." };
var expectOption = new Option<string?>("--expect") { Description = "Wait until this text appears on screen, and fail if it does not." };
var resizeOption = new Option<string?>("--resize") { Description = "Resize the window first, as COLSxROWS." };
var linesOption = new Option<int>("--lines") { Description = "Dump this much scrollback instead of the visible screen.", DefaultValueFactory = _ => 0 };
var waitOption = new Option<int>("--wait") { Description = "Seconds to wait for --expect.", DefaultValueFactory = _ => 15 };

var terminal = new Command("terminal", "Open a shell and dump what the terminal shows.")
    { target, runOption, expectOption, resizeOption, linesOption, waitOption };
terminal.SetAction(async (parsed, cancellationToken) =>
{
    var size = ParseSize(parsed.GetValue(resizeOption));
    if (parsed.GetValue(resizeOption) is not null && size is null)
    {
        Console.Error.WriteLine("A size is COLSxROWS.");
        return 1;
    }

    try
    {
        using var session = Open(parsed);
        using var channel = SshTerminalChannel.Open(session);
        using var pane = new TerminalSession(channel);
        // Read in the background: the engine has to be fed while we wait, because
        // what it shows is the thing under test.
        var pump = pane.RunAsync(cancellationToken);

        // Wait for the far end to say something first. A window-change request
        // sent before the server has finished allocating its pty has nothing to
        // act on, and is simply lost.
        var ready = DateTime.UtcNow.AddSeconds(5);
        while (pane.VisibleText.Trim().Length == 0 && DateTime.UtcNow < ready)
            await Task.Delay(50, cancellationToken);

        if (size is { } wanted)
        {
            pane.Resize(wanted.Columns, wanted.Rows);
            // Give the far end a moment to redraw at the new size before typing
            // into it, or the answer describes the old one.
            await Task.Delay(500, cancellationToken);
        }
        if (parsed.GetValue(runOption) is { } line)
        {
            // Carriage return, because that is what a terminal sends when a person
            // presses Enter. A Unix pty translates a line feed for us; Windows
            // does not, so a line feed there is typed and never run.
            pane.Send(line + "\r");
        }

        var expected = parsed.GetValue(expectOption);
        var deadline = DateTime.UtcNow.AddSeconds(parsed.GetValue(waitOption));
        var found = expected is null;
        while (!found && DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            found = (pane.RecentText() ?? pane.VisibleText).Contains(expected!, StringComparison.Ordinal);
            if (!found)
                await Task.Delay(100, cancellationToken);
        }

        var lines = parsed.GetValue(linesOption);
        Console.WriteLine(lines > 0 ? pane.RecentText(lines) ?? "" : pane.VisibleText);
        if (!found)
            Console.Error.WriteLine($"The terminal never showed {expected}.");
        return found ? 0 : 1;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine(SshFailure.Classify(e).Summary);
        return 1;
    }
});
root.Add(terminal);

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

// ------------------------------------------------------------- workspace
//
// A folder on the host, with the rule that nothing outside it is touched --
// the same RemoteWorkspace the pane and the assistant's file tools both go
// through. Here so the integration gate can prove the rule against a real
// server: that a path climbing out of the root is refused before anything is
// sent, and that a file saved from here comes back byte for byte.
var rootOption = new Option<string>("--root")
{
    Description = "The folder to open, absolute or relative to the account's own directory.",
    Required = true,
};
var listOption = new Option<string?>("--list") { Description = "List this directory inside the workspace." };
var catOption = new Option<string?>("--cat") { Description = "Print this file from inside the workspace." };
var writeOption = new Option<string?>("--write") { Description = "Write this file inside the workspace." };
var contentOption = new Option<string?>("--content") { Description = "What to write, as text." };

var workspace = new Command("workspace", "Open a folder on the host and read or write inside it.")
{
    target, rootOption, listOption, catOption, writeOption, contentOption,
};
workspace.SetAction(parsed => WithSession(parsed, session =>
{
    var folder = new RemoteWorkspace(new SftpFiles(session.OpenSftp()), parsed.GetValue(rootOption)!);
    Console.WriteLine($"root={folder.Root}");

    try
    {
        if (parsed.GetValue(listOption) is { } directory)
        {
            foreach (var entry in folder.List(directory))
                Console.WriteLine($"{(entry.IsDirectory ? "d" : "-")} {folder.Relative(entry.Path)}");
        }

        if (parsed.GetValue(writeOption) is { } written)
        {
            folder.Write(written, parsed.GetValue(contentOption) ?? "");
            Console.WriteLine($"wrote={written}");
        }

        if (parsed.GetValue(catOption) is { } file)
        {
            var read = folder.Read(file);
            Console.WriteLine($"bytes={read.Length} binary={(read.IsBinary ? "yes" : "no")} newline={(read.Newline == "\n" ? "lf" : "crlf")}");
            Console.Write(read.Text);
        }
    }
    catch (WorkspaceBoundsException e)
    {
        // The refusal is the point, so it is reported as one rather than as a
        // crash: the integration gate asserts on this line.
        Console.WriteLine($"refused={e.Message}");
        return 0;
    }

    return 0;
}));
root.Add(workspace);

// ------------------------------------------------------------------- ask
//
// One real assistant turn against a real host and a real provider. Every piece
// of the assistant has unit tests and both providers are driven end to end
// against a stub, but nothing in a test says whether a live exchange works --
// that needs an account, and this is the command that spends one.
//
// STRANGESHARPTERM_ASSIST_PROVIDER, _MODEL and _ENDPOINT redirect it, which is
// how a provider's wire format is checked against a stub without an account.
var questionOption = new Option<string>("--question") { Description = "What to ask about the host.", Required = true };
var contextOption = new Option<bool>("--dump-context") { Description = "Print the block verbatim before it is sent." };
var runCommandsOption = new Option<bool>("--run-commands") { Description = "Give the model the tool that runs commands." };
var approveOption = new Option<bool>("--approve") { Description = "Answer every gate with yes. For a throwaway server only." };

var ask = new Command("ask", "Ask an assistant about the host.")
{
    target, questionOption, contextOption, runCommandsOption, approveOption,
};
ask.SetAction(parsed => WithSession(parsed, session =>
{
    var settings = new AssistSettings().WithEnvironmentOverrides();
    if (AssistBackends.For(settings) is not { } backend)
    {
        Console.Error.WriteLine(AssistBackends.NoKey(settings));
        return 2;
    }

    var alias = parsed.GetValue(target)!;
    var host = new StctlHost(alias, session);
    var approved = parsed.GetValue(approveOption);
    var agent = new HostAgent(backend, host, settings, new StandingAnswer(approved));

    Console.WriteLine($"{backend.ProviderName} · {backend.Model}");

    if (parsed.GetValue(contextOption))
    {
        var context = agent.Context().GetAwaiter().GetResult();
        Console.WriteLine("--- what gets sent ---");
        Console.WriteLine(context.Render());
        Console.WriteLine($"--- {context.Redactions} secret(s) removed ---");
    }

    // Printed as they happen rather than at the end: a run that stops at a gate
    // with --approve off should say what it was stopped on.
    agent.Added += (_, entry) =>
    {
        if (entry is TranscriptEntry.Step step)
            Console.WriteLine($"$ {step.Command}   ({(step.RanUnattended ? "auto" : step.Gate)})");
        else if (entry is TranscriptEntry.Note note)
            Console.WriteLine($"! {note.Text}");
    };

    var answer = agent.Ask(
        parsed.GetValue(questionOption)!,
        new AskOptions { MayRunCommands = parsed.GetValue(runCommandsOption) }).GetAwaiter().GetResult();

    Console.WriteLine();
    Console.WriteLine(answer.Text);
    Console.WriteLine();
    Console.WriteLine($"commands={answer.CommandsRun}");

    return answer.Failed ? 1 : 0;
}));
root.Add(ask);

// ------------------------------------------------------------------- mcp
//
// Connected tool servers, driven without a window. Both transports have to be
// exercised against real servers, and the failures worth finding -- a command
// that is not on the path, a server that dies during startup, a tool reporting
// its own failure -- are not ones a unit test stands in for.
var serverOption = new Option<string[]>("--server")
{
    Description = "A server to connect, as Name=command args, or Name=https://host/mcp. Repeatable.",
    Required = true,
    AllowMultipleArgumentsPerToken = true,
};
var callOption = new Option<string?>("--call") { Description = "Call this tool, by its namespaced name." };
var argumentsOption = new Option<string>("--arguments")
{
    Description = "The tool's arguments, as JSON.",
    DefaultValueFactory = _ => "{}",
};

var mcp = new Command("mcp", "Connect tool servers and report what they offer.")
{
    serverOption, callOption, argumentsOption,
};
mcp.SetAction(parsed =>
{
    var settings = new McpSettings
    {
        Servers = [.. parsed.GetValue(serverOption)!.Select(ParseServer).OfType<McpServerConfig>()],
    };
    if (settings.Servers.Count == 0)
    {
        Console.Error.WriteLine("No server could be read. Use Name=command args, or Name=https://host/mcp.");
        return 2;
    }

    var hub = new McpHub(settings);
    hub.Connect().GetAwaiter().GetResult();

    foreach (var status in hub.Statuses)
    {
        Console.WriteLine($"{status.Config.Name}: {status.Summary}{(status.Failure is { } why ? $" — {why}" : "")}");
        foreach (var offered in hub.ToolsOf(status.Config))
            Console.WriteLine($"  {offered.QualifiedName}");
    }

    var failed = hub.Statuses.Any(status => !status.IsConnected);

    // --call bypasses the gate, which is why it is a flag and nothing the UI
    // can reach.
    if (parsed.GetValue(callOption) is { Length: > 0 } tool)
    {
        if (!hub.Owns(tool))
        {
            Console.Error.WriteLine($"No connected server offers {tool}.");
            hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 2;
        }

        var reply = hub.Call(tool, parsed.GetValue(argumentsOption)!).GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine(reply.Output);
        failed |= reply.Failed;
    }

    hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
    return failed ? 1 : 0;
});
root.Add(mcp);

// --------------------------------------------------------- mcp sign-in
var signIn = new Command("mcp-signin", "Walk a hosted server's OAuth chain and keep the result.")
{
    serverOption,
};
signIn.SetAction(parsed =>
{
    if (parsed.GetValue(serverOption)!.Select(ParseServer).OfType<McpServerConfig>().FirstOrDefault() is not { } server)
    {
        Console.Error.WriteLine("No server could be read.");
        return 2;
    }

    try
    {
        McpSignIn.SignIn(server, openBrowser: url => Console.WriteLine($"Open this: {url}"))
            .GetAwaiter().GetResult();
        Console.WriteLine($"{server.Name}: signed in.");
        return 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
});
root.Add(signIn);

return root.Parse(args).Invoke();

/// <summary>COLSxROWS, as people write a terminal size.</summary>
static (int Columns, int Rows)? ParseSize(string? text)
{
    if (text is null)
        return null;
    var parts = text.Split('x', 'X');
    return parts.Length == 2 && int.TryParse(parts[0], out var columns) && int.TryParse(parts[1], out var rows)
        ? (columns, rows)
        : null;
}

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

/// <summary><c>Name=command args</c>, or <c>Name=https://host/mcp</c>.</summary>
static McpServerConfig? ParseServer(string specification)
{
    var equals = specification.IndexOf('=');
    if (equals <= 0 || equals == specification.Length - 1)
        return null;

    var name = specification[..equals].Trim();
    var rest = specification[(equals + 1)..].Trim();

    if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        return new McpServerConfig { Name = name, Transport = McpTransport.Http, Url = rest };
    }

    var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return words.Length == 0
        ? null
        : new McpServerConfig { Name = name, Command = words[0], Arguments = [.. words[1..]] };
}

/// <summary>
/// <see cref="IHostAccess"/> over one stctl session.
///
/// There is no terminal here, so a question carries the host's name, its kernel
/// and its metrics and nothing else -- which is also the smallest thing that
/// proves the path works.
/// </summary>
internal sealed class StctlHost(string alias, SshNetSession session) : IHostAccess
{
    public string Alias => alias;

    public Task<HostSnapshot> Look(bool metrics, bool terminalTail, int tailLines, CancellationToken cancellationToken = default)
    {
        var kernel = session.Run(HostContext.KernelCommand, TimeSpan.FromSeconds(10)).StandardOutput.Trim();
        return Task.FromResult(new HostSnapshot(
            kernel,
            metrics ? ServerProbe.Parse(session.Run(ServerProbe.Command, TimeSpan.FromSeconds(20)).StandardOutput) : null));
    }

    public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = session.Run(command, timeout);
            var output = string.Join(
                "\n",
                new[] { result.StandardOutput, result.StandardError }
                    .Where(stream => stream.Trim().Length > 0)
                    .Select(stream => stream.TrimEnd()));
            return Task.FromResult(new CommandOutcome(result.ExitStatus, output));
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            return Task.FromResult(new CommandOutcome(-1, "", TimedOut: true));
        }
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
