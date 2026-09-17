using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Assist;

/// <summary>What the probe found, all of it optional.</summary>
public sealed record HostSnapshot(string? Kernel = null, ServerMetrics? Metrics = null, string? TerminalTail = null);

/// <summary>What running a command produced.</summary>
/// <param name="TimedOut">
/// The wait ended, not the command. Nothing here can interrupt a command once
/// ssh has it, so this says the pane stopped listening.
/// </param>
public sealed record CommandOutcome(int ExitStatus, string Output, bool TimedOut = false);

/// <summary>
/// The one host a conversation is about.
///
/// The seam the agent is written against, as <see cref="IServerHealth"/> is for
/// the dashboard. What an assistant does with a server that will not answer is a
/// decision, and deciding it should not need a server.
/// </summary>
public interface IHostAccess
{
    /// <summary>The name the inventory gives it, and the only name that leaves the machine.</summary>
    string Alias { get; }

    /// <summary>
    /// Everything a question carries, gathered when the question is asked.
    ///
    /// One call rather than three, because whether they cost one round trip or
    /// three is this implementation's business.
    /// </summary>
    Task<HostSnapshot> Look(bool metrics, bool terminalTail, int tailLines, CancellationToken cancellationToken = default);

    Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>One command waiting on a person, described so they can answer it.</summary>
/// <param name="Host">
/// Named on every one of these. Approving <c>systemctl restart nginx</c> means
/// nothing until you know whose nginx.
/// </param>
/// <param name="Detail">
/// What the substance of it is, where a line of command is not enough to decide
/// by: the lines a write would change. Shown in the bar itself, for the same
/// reason a tool call's arguments are — a gate whose substance is one click away
/// is a gate people approve without reading.
/// </param>
public sealed record PendingCommand(
    string Host,
    string Command,
    string Why,
    string Reason,
    bool IsDestructive,
    string? Detail = null);

/// <summary>
/// Who says yes.
///
/// A seam rather than a dialog call, because the same agent runs behind a pane
/// where a person answers, behind an orchestrated run where the question is
/// captioned with its host, and in a test where nobody does.
/// </summary>
public interface ICommandGate
{
    Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same gate, for a call to a connected tool.
    ///
    /// It defaults to no. An implementation written before connected tools
    /// existed cannot have an opinion about one, and the safe reading of no
    /// opinion is that the call does not happen.
    /// </summary>
    Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolApproval.No);
}

/// <summary>Answers every gate the same way. For tests, and for the flag that drives a real run without a person.</summary>
public sealed class StandingAnswer(bool answer) : ICommandGate
{
    public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(answer);

    public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default) =>
        // Once, never Always: a standing pass is a thing a person grants, and
        // a flag answering every gate yes must not quietly become one.
        Task.FromResult(answer ? ToolApproval.Once : ToolApproval.No);
}

/// <summary>How many commands are left.</summary>
public interface ICommandBudget
{
    int Remaining { get; }

    /// <summary>Takes one if there is one. False means the budget is spent.</summary>
    bool Take();

    /// <summary>Puts one back. Only <see cref="SharedBudget"/> needs this, and see there for why.</summary>
    void Give();
}

/// <summary>
/// A plain count, which is what a single question gets.
///
/// Taking is one atomic step rather than a check and an increment. A run across
/// several hosts shares one of these between three agents at once, and the
/// racing version of this overspent and underspent by turns -- a fan-out is
/// exactly where a budget has to be exact.
/// </summary>
public sealed class CommandBudget(int total) : ICommandBudget
{
    private int _spent;

    public int Remaining => Math.Max(0, total - Volatile.Read(ref _spent));

    public bool Take()
    {
        while (true)
        {
            var spent = Volatile.Read(ref _spent);
            if (spent >= total)
                return false;
            if (Interlocked.CompareExchange(ref _spent, spent + 1, spent) == spent)
                return true;
        }
    }

    public void Give() => Interlocked.Decrement(ref _spent);
}

/// <summary>
/// Two budgets at once: a host's twelve and the run's sixty.
///
/// The host's is taken first, because it is uncontended -- one agent asks for
/// one command at a time -- and the run's is the one several hosts are competing
/// for. If the run's is spent, the host's is given back: a budget consumed by a
/// command that never ran would quietly shorten the investigation.
/// </summary>
public sealed class SharedBudget(ICommandBudget outer, ICommandBudget inner) : ICommandBudget
{
    public int Remaining => Math.Min(outer.Remaining, inner.Remaining);

    public bool Take()
    {
        if (!inner.Take())
            return false;
        if (outer.Take())
            return true;

        inner.Give();
        return false;
    }

    public void Give()
    {
        inner.Give();
        outer.Give();
    }
}
