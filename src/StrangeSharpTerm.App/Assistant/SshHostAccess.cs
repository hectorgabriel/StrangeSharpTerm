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
public sealed class SshHostAccess(
    string alias,
    IRemoteCommands commands,
    IServerHealth health,
    Func<string?> readTail) : IHostAccess
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
                    var result = commands.Run(command, timeout);
                    return new CommandOutcome(result.ExitStatus, Both(result));
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
