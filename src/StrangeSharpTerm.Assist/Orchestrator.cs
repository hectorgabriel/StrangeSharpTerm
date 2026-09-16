using System.Text;

namespace StrangeSharpTerm.Assist;

/// <summary>How a host came out of a run.</summary>
public enum HostOutcome
{
    /// <summary>It was asked and it answered.</summary>
    Reported,

    /// <summary>It was asked and something went wrong.</summary>
    Failed,

    /// <summary>
    /// It was not asked. Almost always because it is not connected: connecting
    /// can raise a host-key decision, and a fan-out that stopped on a dialog per
    /// host would be worse than one that says plainly which hosts it left out.
    /// </summary>
    NotAsked,

    /// <summary>
    /// A person stopped the run before it finished with this host.
    ///
    /// Its own outcome rather than one of the three above, because it is none of
    /// them: it did not fail, it was not skipped, and whatever it had found by
    /// then is not an answer to the question. Folding it into "failed" accused
    /// every host in the run of breaking whenever somebody pressed Stop.
    /// </summary>
    Stopped,
}

/// <summary>A host a run may reach.</summary>
/// <param name="Agent">
/// Built when the host is actually asked, so a run over eight hosts does not
/// build eight conversations it will not use.
/// </param>
/// <summary>
/// A host a run may reach, and how to get an agent for it.
///
/// No "is it connected" any more: the run connects what it needs. A host that
/// cannot be reached reports why in its own row, which is more use than being
/// left out of a run silently.
/// </summary>
/// <param name="Agent">
/// Built when the host is actually asked, so a run over eight hosts does not
/// build eight conversations it will not use. Null when there is nothing to
/// build one from -- a host the caller can no longer resolve.
/// </param>
/// <param name="Missing">
/// What to say when <paramref name="Agent"/> comes back with nothing.
///
/// Supplied by the caller because only the caller knows why. This used to be an
/// exception thrown from inside the func, which the run caught and reported as
/// a failure -- so a host that was merely gone was accused of breaking, and the
/// developer's own sentence was what the user read.
/// </param>
public sealed record OrchestratorTarget(
    string Alias,
    Func<HostAgent?> Agent,
    string Missing = "It could not be asked.");

/// <summary>What one host contributed.</summary>
public sealed record HostFinding(string Alias, HostOutcome Outcome, string Text, int CommandsRun = 0)
{
    /// <summary>
    /// What a host's answer amounts to, in the words its row shows.
    ///
    /// Here rather than in each caller because there are two of them -- a
    /// fan-out and a planned run -- and they disagreed: the same stopped host
    /// read as "failed" in one and "not asked" in the other.
    ///
    /// A stopped host says only that. Whatever it had said before the
    /// interruption is in its transcript, where the exchange can be opened; it
    /// is not an answer to the question, and putting it in the row would read
    /// like one.
    /// </summary>
    public static HostFinding From(string alias, AgentAnswer answer) => answer switch
    {
        { Stopped: true } => new HostFinding(alias, HostOutcome.Stopped, "Stopped.", answer.CommandsRun),
        { Failed: true } => new HostFinding(
            alias, HostOutcome.Failed, answer.Failure ?? "It did not answer.", answer.CommandsRun),
        { Text.Length: 0 } => new HostFinding(
            alias, HostOutcome.Failed, "It returned nothing.", answer.CommandsRun),
        _ => new HostFinding(alias, HostOutcome.Reported, answer.Text, answer.CommandsRun),
    };

    /// <summary>The word the row shows on the right: "reported", "failed", "stopped", "not asked".</summary>
    public string Label => Outcome switch
    {
        HostOutcome.Reported => "reported",
        HostOutcome.Failed => "failed",
        HostOutcome.Stopped => "stopped",
        _ => "not asked",
    };
}

/// <summary>One instruction carried out across several hosts, and the answer collated from it.</summary>
public sealed record OrchestratedRun(
    string Instruction,
    IReadOnlyList<HostFinding> Findings,
    string Collated,
    int CommandsRun)
{
    /// <summary>
    /// How many were actually put a question. A stopped host counts: it was
    /// asked, and interrupting it does not unask it.
    /// </summary>
    public int HostsAsked => Findings.Count(finding => finding.Outcome != HostOutcome.NotAsked);
}

/// <summary>
/// One question across several hosts.
///
/// It runs nothing itself. Each target gets its own <see cref="HostAgent"/> --
/// the same per-host agent a pane uses, with the same gate, the same budget, the
/// same redaction and the same timeout -- and this makes one further call to
/// collate what they reported.
///
/// Three at a time, and sixty commands for the whole run on top of each host's
/// own twelve. A fan-out multiplies everything, and every host in flight is
/// another gate that can stop for a person.
/// </summary>
public sealed class Orchestrator(IAssistBackend collator)
{
    /// <summary>
    /// What this orchestrator has already been asked and already answered.
    ///
    /// The pane holds one of these for as long as it is open, so a second
    /// instruction is a second turn rather than a first one: "and now the other
    /// two" means something, and the answer can refer to what the last run
    /// found. Each host's own conversation is its agent's; this is the
    /// orchestrator's.
    /// </summary>
    private readonly List<AssistMessage> _conversation = [];

    /// <summary>
    /// What happened somewhere this orchestrator did not itself ask -- a plan
    /// run, whose hosts reported to the runner rather than to this. Folded into
    /// the next report rather than sent alone, for the same reason the planner
    /// does it: a conversation alternates.
    /// </summary>
    private string _reported = "";

    /// <inheritdoc cref="Planner.Record"/>
    public void Record(string whatHappened)
    {
        if (whatHappened.Trim().Length == 0)
            return;
        _reported = _reported.Length == 0 ? whatHappened : $"{_reported}\n\n{whatHappened}";
    }

    /// <summary>A host finished, so a pane can fill its row in before the rest are done.</summary>
    public event EventHandler<HostFinding>? Reported;

    /// <summary>
    /// The collator's reasoning while it writes the answer, where the provider
    /// offers it. Each host's own reasoning is on its own finding; this is the
    /// step that decides what the run amounts to.
    /// </summary>
    public event EventHandler<string>? Thought;

    public async Task<OrchestratedRun> Ask(
        string instruction,
        IReadOnlyList<OrchestratorTarget> targets,
        bool mayRunCommands,
        CancellationToken cancellationToken = default)
    {
        var runBudget = new CommandBudget(AssistLimits.RunBudget);
        var findings = new HostFinding?[targets.Count];
        using var atOnce = new SemaphoreSlim(AssistLimits.Concurrency);

        await Task.WhenAll(targets.Select(async (target, index) =>
        {
            // Inside the try, not before it. Waiting for a slot is the longest
            // a host spends in this method -- five hosts at a concurrency of
            // three means two of them are only ever queued -- and cancelling
            // there used to throw straight out of the fan-out, taking the whole
            // run with it: the hosts that had already answered were on screen,
            // but nothing was collated and the planner was never told.
            var held = false;
            try
            {
                await atOnce.WaitAsync(cancellationToken);
                held = true;

                if (target.Agent() is not { } agent)
                {
                    // Not asked rather than failed: nothing went wrong on the
                    // host, there was simply nothing here to ask it with.
                    findings[index] = Report(
                        new HostFinding(target.Alias, HostOutcome.NotAsked, target.Missing));
                    return;
                }

                var answer = await agent.Ask(
                    instruction,
                    new AskOptions
                    {
                        MayRunCommands = mayRunCommands,
                        // Both budgets: this host's twelve and what is left of
                        // the run's sixty, whichever runs out first.
                        Budget = new SharedBudget(runBudget, new CommandBudget(AssistLimits.CommandBudget)),
                        // A worker in a run is told the connected tools are not
                        // part of its machine and that writing is not its job:
                        // findings go in the report, and the user decides once,
                        // with the whole picture.
                        ToolNote = AssistPrompts.ConnectedToolsInARun,
                    },
                    cancellationToken);

                findings[index] = Report(HostFinding.From(target.Alias, answer));
            }
            catch (OperationCanceledException)
            {
                findings[index] = Report(new HostFinding(target.Alias, HostOutcome.Stopped, "Stopped."));
            }
            catch (Exception e)
            {
                findings[index] = Report(new HostFinding(target.Alias, HostOutcome.Failed, e.Message));
            }
            finally
            {
                // Only what was taken. A host cancelled while queued never got a
                // slot, and releasing one it does not hold would raise the
                // concurrency limit for the rest of the run -- which is the one
                // number standing between a fan-out and eight servers at once.
                if (held)
                    atOnce.Release();
            }
        }));

        var reported = findings.OfType<HostFinding>().ToArray();
        var collated = await Collate(instruction, reported, cancellationToken);

        return new OrchestratedRun(
            instruction, reported, collated, reported.Sum(finding => finding.CommandsRun));
    }

    private HostFinding Report(HostFinding finding)
    {
        Reported?.Invoke(this, finding);
        return finding;
    }

    /// <summary>
    /// The one further call.
    ///
    /// Every host is in the message, including the ones that were not asked:
    /// what the summariser must not do is generalise from the three that
    /// answered to the ten that were ticked, and it cannot avoid that without
    /// being told which is which.
    /// </summary>
    private async Task<string> Collate(
        string instruction,
        IReadOnlyList<HostFinding> findings,
        CancellationToken cancellationToken)
    {
        if (findings.All(finding => finding.Outcome != HostOutcome.Reported))
            return "No host reported, so there is nothing to collate.";

        // Line feeds, not AppendLine's: what a provider is sent must not depend
        // on the operating system the window happens to be running on. The same
        // rule as HostContext.Render, and the same reason.
        List<string> report = [$"The instruction was: {instruction}", ""];

        foreach (var finding in findings)
        {
            report.Add($"## {finding.Alias} ({finding.Label})");
            report.Add(finding.Text);
            report.Add("");
        }

        var said = new StringBuilder();
        var thought = new StringBuilder();
        var asked = new AssistMessage
        {
            Role = AssistRole.User,
            Text = _reported.Length == 0
                ? string.Join('\n', report)
                : $"Since the last answer:\n\n{_reported}\n\n{string.Join('\n', report)}",
        };
        try
        {
            await foreach (var streamed in collator.Stream(
                new AssistRequest
                {
                    System = AssistPrompts.Collator,
                    Messages = [.. _conversation, asked],
                },
                cancellationToken))
            {
                switch (streamed)
                {
                    case AssistEvent.Say say:
                        said.Append(say.Text);
                        break;
                    case AssistEvent.Reasoning reasoning:
                        thought.Append(reasoning.Text);
                        Thought?.Invoke(this, thought.ToString());
                        break;
                }
            }
        }
        catch (AssistException e)
        {
            // Each host's own finding is still on screen and still true. Losing
            // the summary is not losing the run.
            return $"Each host reported, but the summary could not be written: {e.Message}";
        }

        // Committed only once it answered, so a failed turn does not leave a
        // question in the history with nothing after it.
        _conversation.Add(asked);
        _conversation.Add(new AssistMessage { Role = AssistRole.Assistant, Text = said.ToString() });
        _reported = "";
        return said.ToString();
    }
}
