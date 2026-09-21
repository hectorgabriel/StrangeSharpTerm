using System.Text.RegularExpressions;

namespace StrangeSharpTerm.Assist;

/// <summary>Why a phase did not run, or did not get anywhere.</summary>
public enum PhaseOutcome
{
    Completed,

    /// <summary>Switched off before the run started.</summary>
    Disabled,

    /// <summary>No host completed it, which ends the run.</summary>
    Stalled,

    /// <summary>It declared a value and produced none, which also ends the run.</summary>
    NothingCaptured,

    /// <summary>A placeholder in it never got a value, so it was not sent.</summary>
    Unfilled,

    /// <summary>An earlier phase ended the run.</summary>
    NotReached,
}

/// <summary>What one phase did.</summary>
public sealed record PhaseResult(
    PlanPhase Phase,
    PhaseOutcome Outcome,
    IReadOnlyList<HostFinding> Findings,
    string? Captured = null,
    string? CapturedName = null,
    string? Note = null);

/// <summary>A whole planned run.</summary>
public sealed record PlanRunResult(IReadOnlyList<PhaseResult> Phases, bool Stopped, string? StoppedBecause = null);

/// <summary>
/// Carries out a plan.
///
/// Phases go in order and the hosts within a phase go three at a time, because a
/// plan is read downwards and each phase's results stay on screen as the next
/// one starts.
///
/// The run stops if a phase gets nowhere. Joining nodes to a control plane that
/// never came up is worse than stopping, so a phase where no host completed --
/// or one that did not produce the value later phases need -- ends the run and
/// says so. What was already done stays on screen.
/// </summary>
public sealed partial class PlanRunner(Func<string, HostAgent?> agentFor)
{
    /// <summary>
    /// The instruction appended to a capturing phase.
    ///
    /// The captured value is the one thing in a run the user neither wrote nor
    /// read before a machine acted on it, so it is asked for in a form that can
    /// be found exactly rather than parsed out of prose.
    /// </summary>
    internal const string CaptureMarker = "CAPTURED:";

    /// <summary>A phase finished, so the pane can draw it before the next one starts.</summary>
    public event EventHandler<PhaseResult>? Finished;

    /// <summary>A host inside the current phase finished.</summary>
    public event EventHandler<HostFinding>? Reported;

    /// <summary>
    /// A phase is about to be sent to its hosts.
    ///
    /// The pane needs the moment, not just the outcome: each host keeps one
    /// conversation across the whole plan, so a row that showed "this host's
    /// transcript" would show every phase at once. Knowing when a phase starts
    /// is what lets a row show only its own.
    /// </summary>
    public event EventHandler<PlanPhase>? Starting;

    /// <summary>
    /// Runs a plan, phase by phase.
    /// </summary>
    /// <remarks>
    /// There is no "may run commands" here, and there used to be. It was the
    /// pane's Run commands switch passed straight through, and with it off each
    /// host was told "run these commands, in order" and offered nothing to run
    /// them with -- so a real model answered in prose, no command reached any
    /// server, and the phase was recorded as done. A plan is a list of commands
    /// somebody read and then pressed Run on; that press is the consent. What
    /// it is not is a standing pass: every command still meets
    /// <see cref="CommandPolicy"/> and stops at the gate unless it only reads,
    /// exactly as it would in the pane about one host.
    /// </remarks>
    public async Task<PlanRunResult> Run(
        RunPlan plan,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        List<PhaseResult> results = [];
        string? stopped = null;

        foreach (var phase in plan.Phases)
        {
            if (stopped is not null)
            {
                results.Add(Record(new PhaseResult(phase, PhaseOutcome.NotReached, [])));
                continue;
            }

            if (!phase.IsEnabled)
            {
                results.Add(Record(new PhaseResult(phase, PhaseOutcome.Disabled, [])));
                continue;
            }

            // A phase is skipped rather than run with a placeholder still in it.
            // Sending {{join_command}} to a server is the kind of mistake that is
            // obvious afterwards and invisible beforehand.
            if (phase.Placeholders.FirstOrDefault(name => !values.ContainsKey(name)) is { } missing)
            {
                results.Add(Record(new PhaseResult(
                    phase, PhaseOutcome.Unfilled, [],
                    Note: $"{{{{{missing}}}}} never got a value, so this phase was not sent.")));
                stopped = $"{phase.Name} needed {missing}, which no earlier phase produced.";
                continue;
            }

            // The commands are the instruction. They were read and approved as
            // written, so they are handed over as written rather than described
            // back to the model, which would invite it to write its own.
            var commands = phase.Commands.Select(command => RunPlan.Fill(command, values)).ToArray();
            var task = string.Join("\n", [
                .. new[] { "Run these commands on this host, in order:", "" },
                .. commands.Select(command => $"  {command}"),
                "",
                "Run them as they are written. If one fails, stop and report what happened"
                    + " rather than trying something else.",
            ]);
            var instruction = phase.Capture is { Length: > 0 } capture
                ? $"{task}\n\nWhen you are done, end your answer with a line reading "
                    + $"{CaptureMarker} followed by the {capture} and nothing else."
                : task;

            Starting?.Invoke(this, phase);
            var findings = await Carry(phase, instruction, cancellationToken);
            var completed = findings.Where(finding => finding.Outcome == HostOutcome.Reported).ToArray();

            if (completed.Length == 0)
            {
                results.Add(Record(new PhaseResult(phase, PhaseOutcome.Stalled, findings)));
                stopped = findings.Any(finding => finding.RanNothing)
                    ? $"No host ran the commands in {phase.Name}."
                    : $"No host completed {phase.Name}.";
                continue;
            }

            if (phase.Capture is not { Length: > 0 } name)
            {
                results.Add(Record(new PhaseResult(phase, PhaseOutcome.Completed, findings)));
                continue;
            }

            if (Captured(completed[0].Text) is not { Length: > 0 } value)
            {
                results.Add(Record(new PhaseResult(
                    phase, PhaseOutcome.NothingCaptured, findings,
                    Note: $"{phase.Hosts[0]} did not report a {name}.")));
                stopped = $"{phase.Name} was meant to produce {name} and did not.";
                continue;
            }

            values[name] = value;
            results.Add(Record(new PhaseResult(
                phase, PhaseOutcome.Completed, findings, Captured: value, CapturedName: name)));
        }

        return new PlanRunResult(results, stopped is not null, stopped);
    }

    private async Task<IReadOnlyList<HostFinding>> Carry(
        PlanPhase phase,
        string instruction,
        CancellationToken cancellationToken)
    {
        var findings = new HostFinding?[phase.Hosts.Count];
        using var atOnce = new SemaphoreSlim(AssistLimits.Concurrency);

        await Task.WhenAll(phase.Hosts.Select(async (alias, index) =>
        {
            if (agentFor(alias) is not { } agent)
            {
                findings[index] = Say(new HostFinding(
                    alias, HostOutcome.NotAsked, "Not connected — connect it and run the plan again."));
                return;
            }

            var held = false;
            try
            {
                // Inside the try, as the fan-out has it: cancelling a host that
                // is still queued must leave a row saying so, not throw out of
                // the phase and lose what the other hosts did.
                await atOnce.WaitAsync(cancellationToken);
                held = true;
                var answer = await agent.Ask(
                    instruction,
                    new AskOptions
                    {
                        // Always: running the phase is running its commands.
                        // See Run for why this is not the pane's switch.
                        MayRunCommands = true,
                        // What the phase lists and a little over, per host, and
                        // nothing run-wide: the plan was read before it ran, so
                        // the plan is the bound. See PlanAllowance.
                        Budget = new CommandBudget(phase.Commands.Count + AssistLimits.PlanAllowance),
                        // Ten minutes rather than one: apt install and kubeadm
                        // init legitimately take that long, and a loop that gave
                        // up after a minute would carry on reading the output of
                        // a command it had stopped waiting for.
                        CommandTimeout = AssistLimits.PlanCommandTimeout,
                        // A worker in a run is told the connected tools are not
                        // part of its machine and that writing is not its job:
                        // findings go in the report, and the user decides once,
                        // with the whole picture.
                        ToolNote = AssistPrompts.ConnectedToolsInARun,
                    },
                    cancellationToken);

                // A host that answered without running a single command has
                // not done the phase, however confidently it says it has. It
                // reads as success on screen, which is the worst way for this
                // to fail -- so it is a failure, with its words kept.
                findings[index] = Say(
                    answer is { CommandsRun: 0, Failed: false, Stopped: false } && phase.Commands.Count > 0
                        ? HostFinding.RanNone(alias, answer)
                        : HostFinding.From(alias, answer));
            }
            catch (OperationCanceledException)
            {
                findings[index] = Say(new HostFinding(alias, HostOutcome.Stopped, "Stopped."));
            }
            catch (Exception e)
            {
                findings[index] = Say(new HostFinding(alias, HostOutcome.Failed, e.Message));
            }
            finally
            {
                if (held)
                    atOnce.Release();
            }
        }));

        return [.. findings.OfType<HostFinding>()];
    }

    /// <summary>
    /// The value a capturing phase reported, from the last line that names it.
    ///
    /// The last rather than the first: a model that explains what it is about to
    /// do and then does it writes the marker twice, and the one that counts is
    /// the one after the work.
    /// </summary>
    internal static string? Captured(string answer) =>
        CapturedLine().Matches(answer) is { Count: > 0 } matches
            ? matches[^1].Groups[1].Value.Trim().Trim('`').Trim()
            : null;

    private PhaseResult Record(PhaseResult result)
    {
        Finished?.Invoke(this, result);
        return result;
    }

    private HostFinding Say(HostFinding finding)
    {
        Reported?.Invoke(this, finding);
        return finding;
    }

    [GeneratedRegex(@"^\s*CAPTURED:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex CapturedLine();
}
