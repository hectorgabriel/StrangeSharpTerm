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
}

/// <summary>A host a run may reach.</summary>
/// <param name="Agent">
/// Built when the host is actually asked, so a run over eight hosts does not
/// build eight conversations it will not use.
/// </param>
public sealed record OrchestratorTarget(string Alias, bool IsConnected, Func<HostAgent> Agent);

/// <summary>What one host contributed.</summary>
public sealed record HostFinding(string Alias, HostOutcome Outcome, string Text, int CommandsRun = 0)
{
    /// <summary>The word the row shows on the right: "reported", "failed", "not asked".</summary>
    public string Label => Outcome switch
    {
        HostOutcome.Reported => "reported",
        HostOutcome.Failed => "failed",
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
    /// <summary>A host finished, so a pane can fill its row in before the rest are done.</summary>
    public event EventHandler<HostFinding>? Reported;

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
            if (!target.IsConnected)
            {
                findings[index] = Report(new HostFinding(
                    target.Alias, HostOutcome.NotAsked, "Not connected — connect it and run again."));
                return;
            }

            await atOnce.WaitAsync(cancellationToken);
            try
            {
                var agent = target.Agent();
                var answer = await agent.Ask(
                    instruction,
                    new AskOptions
                    {
                        MayRunCommands = mayRunCommands,
                        // Both budgets: this host's twelve and what is left of
                        // the run's sixty, whichever runs out first.
                        Budget = new SharedBudget(runBudget, new CommandBudget(AssistLimits.CommandBudget)),
                    },
                    cancellationToken);

                findings[index] = Report(answer switch
                {
                    { Failed: true } => new HostFinding(
                        target.Alias, HostOutcome.Failed, answer.Failure ?? "It did not answer.", answer.CommandsRun),
                    { Text.Length: 0 } => new HostFinding(
                        target.Alias, HostOutcome.Failed, "It returned nothing.", answer.CommandsRun),
                    _ => new HostFinding(target.Alias, HostOutcome.Reported, answer.Text, answer.CommandsRun),
                });
            }
            catch (OperationCanceledException)
            {
                findings[index] = Report(new HostFinding(target.Alias, HostOutcome.NotAsked, "Stopped."));
            }
            catch (Exception e)
            {
                findings[index] = Report(new HostFinding(target.Alias, HostOutcome.Failed, e.Message));
            }
            finally
            {
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

        var report = new StringBuilder();
        report.Append("The instruction was: ").AppendLine(instruction).AppendLine();

        foreach (var finding in findings)
        {
            report.Append("## ").Append(finding.Alias).Append(" (").Append(finding.Label).AppendLine(")");
            report.AppendLine(finding.Text).AppendLine();
        }

        var said = new StringBuilder();
        try
        {
            await foreach (var streamed in collator.Stream(
                new AssistRequest
                {
                    System = AssistPrompts.Collator,
                    Messages = [new AssistMessage { Role = AssistRole.User, Text = report.ToString() }],
                },
                cancellationToken))
            {
                if (streamed is AssistEvent.Say say)
                    said.Append(say.Text);
            }
        }
        catch (AssistException e)
        {
            // Each host's own finding is still on screen and still true. Losing
            // the summary is not losing the run.
            return $"Each host reported, but the summary could not be written: {e.Message}";
        }

        return said.ToString();
    }
}
