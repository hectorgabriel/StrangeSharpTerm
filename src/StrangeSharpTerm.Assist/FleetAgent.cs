using System.Text;

namespace StrangeSharpTerm.Assist;

/// <summary>One host a fleet conversation can reach, and how to reach it.</summary>
/// <param name="Access">
/// Opened when the host is first asked for something, so a run over eight hosts
/// does not connect to eight of them to look at one.
/// </param>
public sealed record FleetHost(string Alias, Func<IHostAccess?> Access);

/// <summary>
/// One conversation about several hosts.
///
/// The orchestrator used to give every ticked host its own <see cref="HostAgent"/>
/// -- its own conversation, its own investigation -- and then make one further
/// call to collate what they each reported. Nine conversations for eight hosts,
/// each of which had to be told what the others found second-hand.
///
/// This is one. It gets <c>run_command</c> with a host argument and decides for
/// itself which machine to look at next, so "is the journal filling the disk
/// anywhere" is one assistant asking eight servers rather than eight assistants
/// each asked the same thing. It can compare what it saw on one host with what
/// it saw on another, which the summariser could only ever do through reports.
///
/// What it does not get is a second way to run a command. Every call goes
/// through the same <see cref="CommandPolicy"/>, the same gate and the same
/// redaction as a command typed in a pane -- the host argument decides where it
/// lands, and nothing else about it changes.
/// </summary>
public sealed class FleetAgent(
    IAssistBackend backend,
    ICommandGate gate,
    IExternalTools? tools = null)
{
    /// <summary>
    /// The hosts this question is about, which are the ones ticked when it was
    /// asked.
    ///
    /// Per question rather than per agent, because the conversation outlives any
    /// one run: "and now the other two" is a second question in the same
    /// conversation, over a different set of machines.
    /// </summary>
    private IReadOnlyList<FleetHost> _hosts = [];

    /// <summary>
    /// What this assistant has already been asked and already answered.
    ///
    /// Kept for as long as the pane is open, so a second instruction is a
    /// second turn rather than a first one: "and now the other two" means
    /// something, and the answer can refer to what the last run found.
    /// </summary>
    private readonly List<AssistMessage> _conversation = [];
    private readonly List<TranscriptEntry> _entries = [];

    /// <summary>Opened once each, and kept: a run asks most hosts more than one thing.</summary>
    private readonly Dictionary<string, IHostAccess?> _opened = new(StringComparer.Ordinal);

    public string ProviderName => backend.ProviderName;

    public string Model => backend.Model;

    public IReadOnlyList<TranscriptEntry> Entries => _entries;

    /// <summary>A row was added.</summary>
    public event EventHandler<TranscriptEntry>? Added;

    /// <summary>A row changed underneath: an answer grew, a command finished.</summary>
    public event EventHandler<TranscriptEntry>? Updated;

    /// <summary>
    /// A command is about to run on a host, and then that it has finished.
    ///
    /// For a window that wants to show which host the assistant is working on:
    /// the pane showing that host can say so while it happens, which is the
    /// only way to see a fleet being driven rather than read about it
    /// afterwards.
    /// </summary>
    public event EventHandler<FleetStep>? Working;

    public async Task<AgentAnswer> Ask(
        string instruction,
        IReadOnlyList<FleetHost> hosts,
        bool mayRunCommands,
        CancellationToken cancellationToken = default)
    {
        _hosts = hosts;

        // The run's budget, not a host's: there are no per-host conversations to
        // give twelve commands each to any more, and a single assistant looking
        // at eight machines spends them where they are needed rather than evenly.
        var budget = new CommandBudget(AssistLimits.RunBudget);
        var ran = 0;
        var found = "";

        Append(new TranscriptEntry.Question(instruction));
        _conversation.Add(new AssistMessage
        {
            Role = AssistRole.User,
            Text = string.Join("\n\n", Roster(), instruction),
        });

        var ceiling = AssistLimits.RunBudget + 4;
        for (var turn = 0; turn < ceiling; turn++)
        {
            var answer = new TranscriptEntry.Answer();
            var calls = new List<AssistToolCall>();
            var said = new StringBuilder();
            var thought = new StringBuilder();
            var shown = false;

            try
            {
                var offered = Offered(mayRunCommands);
                await foreach (var streamed in backend.Stream(
                    new AssistRequest
                    {
                        System = AssistPrompts.Fleet,
                        Messages = [.. _conversation],
                        Tools = offered,
                    },
                    cancellationToken))
                {
                    switch (streamed)
                    {
                        case AssistEvent.Say say:
                            said.Append(say.Text);
                            answer.Markdown = said.ToString();
                            Show();
                            break;

                        case AssistEvent.Reasoning reasoning:
                            thought.Append(reasoning.Text);
                            answer.Reasoning = thought.ToString();
                            Show();
                            break;

                        case AssistEvent.Call call:
                            calls.Add(call.Tool);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Append(new TranscriptEntry.Note("Stopped."));
                return new AgentAnswer(Latest(said, thought, found), ran, Stopped: true);
            }
            catch (AssistException e)
            {
                Append(new TranscriptEntry.Note(e.Message));
                return AgentAnswer.Broken(e.Message);
            }

            _conversation.Add(new AssistMessage
            {
                Role = AssistRole.Assistant,
                Text = said.Length == 0 ? null : said.ToString(),
                ToolCalls = calls,
            });

            found = Latest(said, thought, found);
            if (calls.Count == 0)
                return new AgentAnswer(found, ran);

            var results = new List<AssistToolResult>();
            foreach (var call in calls)
            {
                var (carried, result) = await Carry(call, budget, cancellationToken);
                if (carried)
                    ran++;
                results.Add(result);
            }

            _conversation.Add(new AssistMessage { Role = AssistRole.User, ToolResults = results });

            void Show()
            {
                if (shown)
                {
                    Updated?.Invoke(this, answer);
                    return;
                }
                shown = true;
                Append(answer);
            }
        }

        Append(new TranscriptEntry.Note("This run went on long enough that the assistant stopped."));
        return new AgentAnswer(found, ran);
    }

    /// <summary>
    /// What the hosts are called, which is all it is told before it asks.
    ///
    /// No metrics and no terminal tail. A per-host pane carries them because it
    /// is about the one machine in front of you; probing eight servers before
    /// every question would be eight round trips to answer one, and the
    /// assistant can ask for what it actually needs.
    /// </summary>
    private string Roster() => string.Join('\n',
        ["The hosts you can reach:", .. _hosts.Select(host => $"- {host.Alias}")]);

    private IReadOnlyList<AssistTool> Offered(bool mayRunCommands)
    {
        List<AssistTool> offered = [];
        if (mayRunCommands)
            offered.Add(AssistTools.FleetRunner([.. _hosts.Select(host => host.Alias)]));
        if (tools is { } connected)
            offered.AddRange(connected.Offered);
        return offered;
    }

    /// <summary>One call: on which host, judged, maybe asked about, maybe run.</summary>
    private async Task<(bool Ran, AssistToolResult Result)> Carry(
        AssistToolCall call,
        ICommandBudget budget,
        CancellationToken cancellationToken)
    {
        if (tools is { } connected && connected.Owns(call.Name))
            return (false, new AssistToolResult(call.Id, "Connected tools are not available in a fleet run.", Failed: true));

        if (call.Name != AssistTools.RunCommand)
            return (false, new AssistToolResult(call.Id, $"There is no tool called {call.Name}.", Failed: true));

        var alias = AssistTools.ReadHost(call.Arguments);
        var (command, why) = AssistTools.ReadRun(call.Arguments);
        var judgement = CommandPolicy.Judge(command);
        var step = new TranscriptEntry.Step
        {
            Host = alias,
            Command = command,
            Why = why,
            Gate = judgement.Reason,
            IsDestructive = judgement.IsDestructive,
        };
        Append(step);

        // A host it invented, or one the user unticked. Told plainly rather
        // than run somewhere else.
        if (_hosts.All(host => host.Alias != alias))
        {
            step.State = StepState.Failed;
            step.Output = $"There is no host called {alias} in this run.";
            Updated?.Invoke(this, step);
            return (false, new AssistToolResult(
                call.Id,
                $"There is no host called {alias} in this run. The hosts are: "
                    + $"{string.Join(", ", _hosts.Select(host => host.Alias))}.",
                Failed: true));
        }

        if (!budget.Take())
        {
            step.State = StepState.Skipped;
            Updated?.Invoke(this, step);
            return (false, new AssistToolResult(
                call.Id,
                "The command budget for this run is spent. Do not ask for anything else; "
                    + "summarise what you have found so far.",
                Failed: true));
        }

        if (!judgement.MayRunUnattended)
        {
            var allowed = await gate.Allow(
                new PendingCommand(alias, command, why, judgement.Reason, judgement.IsDestructive),
                cancellationToken);

            if (!allowed)
            {
                step.State = StepState.Refused;
                Updated?.Invoke(this, step);
                return (false, new AssistToolResult(
                    call.Id,
                    "The user refused to run this command. Do not attempt the same thing another way, "
                        + "on this host or on any other. Work with what you already have, or say what "
                        + "you would need and why.",
                    Failed: true));
            }
        }

        if (Open(alias) is not { } access)
        {
            step.State = StepState.Failed;
            step.Output = $"{alias} could not be reached.";
            Updated?.Invoke(this, step);
            return (false, new AssistToolResult(call.Id, $"{alias} could not be reached.", Failed: true));
        }

        step.State = StepState.Running;
        Updated?.Invoke(this, step);
        Working?.Invoke(this, new FleetStep(alias, command, why, Running: true));

        CommandOutcome outcome;
        try
        {
            outcome = await access.Run(command, AssistLimits.CommandTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Working?.Invoke(this, new FleetStep(alias, command, why, Running: false));
            throw;
        }
        catch (Exception e)
        {
            step.State = StepState.Failed;
            step.Output = e.Message;
            Updated?.Invoke(this, step);
            Working?.Invoke(this, new FleetStep(alias, command, why, Running: false));
            return (false, new AssistToolResult(call.Id, $"The command could not be run: {e.Message}", Failed: true));
        }

        var output = HostAgent.Truncate(Redaction.Scrub(outcome.Output).Text);
        step.State = outcome.TimedOut ? StepState.TimedOut : StepState.Ran;
        step.ExitStatus = outcome.TimedOut ? null : outcome.ExitStatus;
        step.Output = output;
        Updated?.Invoke(this, step);
        Working?.Invoke(this, new FleetStep(alias, command, why, Running: false, outcome.ExitStatus, output));

        // Named in the result as well as in the row: one conversation holds
        // every host's output, and a result that did not say which machine it
        // came from would be one the model has to guess about.
        var reply = outcome.TimedOut
            ? $"{alias}: the command did not finish within {AssistLimits.CommandTimeout.TotalSeconds:0} seconds "
                + $"and was left running. Partial output:\n{output}"
            : $"{alias}: exit status {outcome.ExitStatus}\n{output}";

        return (true, new AssistToolResult(call.Id, reply, Failed: outcome.TimedOut));
    }

    private IHostAccess? Open(string alias)
    {
        if (_opened.TryGetValue(alias, out var held))
            return held;
        return _opened[alias] = _hosts.FirstOrDefault(host => host.Alias == alias)?.Access();
    }

    private static string Latest(StringBuilder said, StringBuilder thought, string found) =>
        said.Length > 0 ? said.ToString()
        : thought.Length > 0 ? thought.ToString()
        : found;

    private void Append(TranscriptEntry entry)
    {
        _entries.Add(entry);
        Added?.Invoke(this, entry);
    }
}

/// <summary>
/// A command the fleet assistant is running on a host, and then the same one
/// once it has finished.
/// </summary>
/// <param name="Running">
/// True on the way in and false on the way out, so a pane can show that the
/// assistant has this host and then let go of it again.
/// </param>
public sealed record FleetStep(
    string Host,
    string Command,
    string Why,
    bool Running,
    int? ExitStatus = null,
    string Output = "");
