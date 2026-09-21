using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// <see cref="IHostAccess"/> over a real connection.
///
/// The three things a question carries come from three places the app already
/// has: the alias from the inventory, the metrics from the same probe the
/// dashboard runs, and the tail from the terminal the pane is split beside —
/// read through the engine, so what the assistant reads is exactly what the user
/// is looking at.
///
/// The probe runs when a question is asked rather than on a timer. A pane
/// sitting open is not a reason to poll a server.
/// </summary>
/// <param name="sudoPassword">
/// The password to give <c>sudo</c> on this host, or null where there is none
/// or a person has not said it may be used. Asked for each time rather than
/// held, so turning the switch off takes effect on the next command and the
/// password is not kept on this object for the life of a conversation.
/// </param>
public sealed class SshHostAccess(
    string alias,
    IRemoteCommands commands,
    IServerHealth health,
    Func<string?> readTail,
    Func<string?>? sudoPassword = null) : IHostAccess
{
    public string Alias => alias;

    public async Task<HostSnapshot> Look(
        bool metrics,
        bool terminalTail,
        int tailLines,
        CancellationToken cancellationToken = default)
    {
        var tail = terminalTail ? Blank(readTail()) : null;
        if (!metrics)
            return new HostSnapshot(TerminalTail: tail);

        // Off the UI thread, as a connection is: a window that freezes while a
        // host thinks about it is what that avoids. Each half is guarded on its
        // own, because a server with no `uname` is still a server worth asking
        // about.
        return await Task.Run(
            () =>
            {
                var kernel = Guard(
                    () => commands.Run(HostContext.KernelCommand, TimeSpan.FromSeconds(10)).StandardOutput.Trim(),
                    "its kernel");
                var collected = Guard(() => health.Collect(), "a probe");
                return new HostSnapshot(Blank(kernel), collected, tail);
            },
            cancellationToken);
    }

    public async Task<CommandOutcome> Run(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        await Task.Run(
            () =>
            {
                try
                {
                    var (result, explain) = Execute(command, timeout);
                    var output = Both(result);
                    // Said after the output rather than instead of it: what sudo
                    // said is still the truth about what happened, and this is
                    // the part the model could not have worked out.
                    return new CommandOutcome(
                        result.ExitStatus,
                        explain ? string.Join("\n\n", new[] { output, Sudo.NotGiven }.Where(part => part.Length > 0)) : output);
                }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException)
                {
                    // The command is not killed -- nothing here can kill it --
                    // so what ended is the waiting.
                    return new CommandOutcome(-1, "", TimedOut: true);
                }
            },
            cancellationToken);

    /// <summary>
    /// Runs one command, giving <c>sudo</c> this host's password where -- and
    /// only where -- that is safe.
    ///
    /// The command the model asked for and the transcript shows is the one it
    /// wrote. What runs, when the password goes, is <see cref="Sudo.Prepared"/>
    /// of it: the same line with <c>-k -S -p ''</c> after <c>sudo</c>, and the
    /// password on its input. See <see cref="Sudo"/> for every case it refuses
    /// and why each one is a leak rather than a limitation.
    /// </summary>
    /// <returns>
    /// What ran, and whether the model should be told why sudo did not get the
    /// password: true only where a password was there to give and the command
    /// used sudo in a shape that does not qualify.
    /// </returns>
    private (CommandResult Result, bool Explain) Execute(string command, TimeSpan timeout)
    {
        if (sudoPassword?.Invoke() is not { Length: > 0 } password)
            return (commands.Run(command, timeout), false);

        if (Sudo.Prepared(command) is not { } prepared)
            return (commands.Run(command, timeout), Sudo.Mentions(command));

        // Asked first, with nothing on its input. Where sudo needs no password
        // here, none is sent: it would not be read by sudo, and the command
        // after it would read it instead.
        if (commands.Run(Sudo.Probe, ProbeTimeout).ExitStatus == 0)
            return (commands.Run(command, timeout), false);

        return (commands.RunFeeding(prepared, timeout, password + "\n"), false);
    }

    /// <summary>How long the question "does sudo want a password" may take. It is sudo and true.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Both streams. A command that failed usually says why on the other one,
    /// and a model told only "exit status 1" has been told nothing.
    /// </summary>
    private static string Both(CommandResult result) => string.Join(
        "\n",
        new[] { result.StandardOutput, result.StandardError }
            .Where(stream => stream.Trim().Length > 0)
            .Select(stream => stream.TrimEnd()));

    private T? Guard<T>(Func<T> ask, string what)
    {
        try
        {
            return ask();
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"asking {alias} for {what} failed: {e.Message}");
            return default;
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}

/// <summary>
/// Where the tail comes from: the shell in the pane the assistant was opened
/// beside.
///
/// Read through the terminal's own engine, so what the assistant reads is
/// exactly what the user is looking at, scrollback included. A pane with no
/// shell beside it carries no tail, which the disclosure then shows.
/// </summary>
public static class TerminalTail
{
    public static Func<string?> Of(TerminalRegistry terminals, NodeId? pane, int lines) =>
        Of(terminals, () => pane, lines);

    /// <summary>
    /// The same, for a conversation whose pane is not settled when it is built.
    ///
    /// A docked assistant follows whichever session has the keyboard, so which
    /// shell it is reading is a question to ask each time rather than an answer
    /// to keep.
    /// </summary>
    public static Func<string?> Of(TerminalRegistry terminals, Func<NodeId?> pane, int lines) =>
        () => pane() is { } id ? terminals.RecentText(id, lines) : null;
}
