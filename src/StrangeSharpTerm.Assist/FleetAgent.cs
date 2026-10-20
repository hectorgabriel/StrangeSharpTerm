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
/// <param name="localWorkspace">
/// The folder open on the machine this app is running on, or null.
///
/// This machine's and not the hosts'. A fleet conversation is where one
/// instruction becomes an action on eight servers, and a write fanned out that
/// way from a single approval is the one thing this app should not make easy --
/// changing a file on a server stays a job for the pane about that server. What
/// a run genuinely wants is the other direction: read the runbook here, compare
/// it with what eight machines actually have, and write the findings down
/// somewhere that is not a chat window.
/// </param>
public sealed class FleetAgent(
    IAssistBackend backend,
    ICommandGate gate,
    IExternalTools? tools = null,
    IWorkspaceAccess? localWorkspace = null)
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

    /// <summary>
    /// What this conversation has had taken out of what the hosts said, so a
    /// file it writes here can carry the real value. See <see cref="Secrets"/>.
    /// </summary>
    private readonly Secrets _secrets = new();

    /// <summary>Opened once each, and kept: a run asks most hosts more than one thing.</summary>
    private readonly Dictionary<string, IHostAccess?> _opened = new(StringComparer.Ordinal);

    public string ProviderName => backend.ProviderName;

    public string Model => backend.Model;

    public IReadOnlyList<TranscriptEntry> Entries => _entries;

    /// <summary>A row was added.</summary>
    public event EventHandler<TranscriptEntry>? Added;

    /// <summary>A row changed underneath: an answer grew, a command finished.</summary>
    public event EventHandler<TranscriptEntry>? Updated;

    /// <inheritdoc cref="HostAgent.Clear"/>
    public void Clear()
    {
        _conversation.Clear();
        _entries.Clear();
        _questions.Clear();
        _recap.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Where each question began in the conversation and the transcript, so <see cref="Compact"/>
    /// can drop whole exchanges -- see <see cref="HostAgent"/>, which keeps the
    /// same list for the same reason.
    /// </summary>
    private readonly List<(int Messages, int Entries)> _questions = [];

    /// <inheritdoc cref="Recap"/>
    private readonly Recap _recap = new();

    /// <summary>
    /// Leaves the oldest exchanges out of what is sent once the conversation is
    /// too large to keep sending whole.
    /// </summary>
    /// <remarks>
    /// The same rule as the conversation about one host: whole exchanges, oldest
    /// first, never the newest, and the transcript left alone. It was missing
    /// here, where it matters most -- a run may spend sixty commands of up to
    /// <see cref="AssistLimits.MaxOutputCharacters"/> apiece, so one question
    /// could leave nearly half a million characters behind it, every one of
    /// them resent on each turn of every question after it, until somebody
    /// typed /clear.
    ///
    /// The same bound as well, although one run can outgrow it on its own. That
    /// run is still sent whole, because cutting into it would separate a call
    /// from its result; what the bound promises here is that nothing *earlier*
    /// rides along with it.
    /// </remarks>
    /// <returns>How many exchanges were left out.</returns>
    private int Compact()
    {
        var dropped = 0;
        while (_questions.Count > 1
            && _conversation.Sum(message => message.Size) + _recap.Size > AssistLimits.MaxConversationCharacters)
        {
            _recap.Add(_entries.Skip(_questions[0].Entries).Take(_questions[1].Entries - _questions[0].Entries));

            var from = _questions[0].Messages;
            var count = _questions[1].Messages - from;
            _conversation.RemoveRange(from, count);
            _questions.RemoveAt(0);

            // Everything after it moved up by what was taken out.
            for (var index = 0; index < _questions.Count; index++)
                _questions[index] = (_questions[index].Messages - count, _questions[index].Entries);

            dropped++;
        }

        return dropped;
    }

    /// <summary>Everything was forgotten, so a pane can empty itself.</summary>
    public event EventHandler? Cleared;

    /// <inheritdoc cref="HostAgent.Say"/>
    public void Say(string text) => Append(new TranscriptEntry.Note(text));

    /// <summary>
    /// A command is about to run on a host, and then that it has finished.
    ///
    /// For a window that wants to show which host the assistant is working on:
    /// the pane showing that host can say so while it happens, which is the
    /// only way to see a fleet being driven rather than read about it
    /// afterwards.
    /// </summary>
    public event EventHandler<AssistStep>? Working;

    public async Task<AgentAnswer> Ask(
        string instruction,
        IReadOnlyList<FleetHost> hosts,
        bool mayRunCommands,
        bool mayEditFiles = false,
        CancellationToken cancellationToken = default)
    {
        _hosts = hosts;

        // The run's budget, not a host's: there are no per-host conversations to
        // give twelve commands each to any more, and a single assistant looking
        // at eight machines spends them where they are needed rather than evenly.
        var budget = new CommandBudget(AssistLimits.RunBudget);
        var ran = 0;
        var found = "";

        _questions.Add((_conversation.Count, _entries.Count));

        Append(new TranscriptEntry.Question(instruction));
        _conversation.Add(new AssistMessage
        {
            Role = AssistRole.User,
            Text = string.Join("\n\n", Roster(), instruction),
        });

        // Said once per question -- see HostAgent.Ask.
        var noted = false;

        var ceiling = AssistLimits.RunBudget + 4;
        for (var turn = 0; turn < ceiling; turn++)
        {
            var answer = new TranscriptEntry.Answer();
            var calls = new List<AssistToolCall>();
            var said = new StringBuilder();
            IReadOnlyList<AssistTool> offered = [];
            var thought = new StringBuilder();
            var shown = false;

            try
            {
                // Before the request, because it is this turn's request that
                // has to fit.
                var dropped = Compact();
                if (dropped > 0 && !noted)
                {
                    noted = true;
                    Append(new TranscriptEntry.Note(dropped == 1
                        ? "This conversation is long enough that its earliest instruction was left out of what was sent. A short account of it goes instead."
                        : $"This conversation is long enough that its earliest {dropped} instructions were left out of what was sent. A short account of them goes instead."));
                }

                offered = Offered(mayRunCommands, mayEditFiles);
                await foreach (var streamed in backend.Stream(
                    new AssistRequest
                    {
                        System = System(mayEditFiles),
                        Messages = _recap.Lead(_conversation),
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

            var results = new AssistToolResult[calls.Count];
            foreach (var batch in Batches(calls, offered))
            {
                // Only what was offered this turn -- see HostAgent.Unoffered.
                // With eight hosts in reach this matters more, not less: an
                // uninvited run_command here lands on whichever host the model
                // names.
                if (batch is [var lone] && !offered.Any(tool => tool.Name == calls[lone].Name))
                {
                    results[lone] = Unoffered(calls[lone]);
                    continue;
                }

                if (batch is [var single])
                {
                    var (carried, result) = await Carry(calls[single], budget, cancellationToken);
                    if (carried)
                        ran++;
                    results[single] = result;
                    continue;
                }

                // Reads, side by side, and never more at once than a run puts in
                // flight anywhere else. Each is started here, one after another,
                // and gets as far as its row before the next begins -- so the
                // rows appear in the order they were asked for, which handing
                // them to a pool of workers did not promise.
                using (var throttle = new SemaphoreSlim(AssistLimits.Concurrency))
                {
                    await Task.WhenAll([.. batch.Select(async index =>
                    {
                        var (carried, result) = await Carry(calls[index], budget, cancellationToken, throttle);
                        if (carried)
                            Interlocked.Increment(ref ran);
                        results[index] = result;
                    })]);
                }
            }

            // In the order they were asked for, whatever order they finished in.
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
    /// A turn's calls, in order, as the groups they can run in.
    ///
    /// A command the policy lets through without asking only reads, so
    /// several of them -- the same question put to eight hosts, say -- can run
    /// at once instead of one after another, each waiting for the slowest
    /// server before it. Everything else runs alone, in its place: a call that
    /// stops for a person must not have reads racing past it, and the reads
    /// after it must not start before it is answered, because the person may
    /// be refusing the thing they would have looked at.
    /// </summary>
    private static IEnumerable<int[]> Batches(IReadOnlyList<AssistToolCall> calls, IReadOnlyList<AssistTool> offered)
    {
        List<int> reads = [];
        for (var index = 0; index < calls.Count; index++)
        {
            if (Reads(calls[index], offered))
            {
                reads.Add(index);
                continue;
            }

            if (reads.Count > 0)
                yield return [.. reads];
            reads.Clear();
            yield return [index];
        }

        if (reads.Count > 0)
            yield return [.. reads];
    }

    /// <summary>Whether a call is a command that would run without anybody being asked.</summary>
    private static bool Reads(AssistToolCall call, IReadOnlyList<AssistTool> offered) =>
        call.Name == AssistTools.RunCommand
        && offered.Any(tool => tool.Name == call.Name)
        && CommandPolicy.Judge(AssistTools.ReadRun(call.Arguments).Command).MayRunUnattended;

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

    /// <summary>
    /// The folder on this machine, or null when nobody has opened one.
    ///
    /// Asked each turn rather than once, because a folder is opened and closed
    /// while a conversation is going on.
    /// </summary>
    private IWorkspaceAccess? Here => localWorkspace is { Root.Length: > 0 } open ? open : null;

    private IReadOnlyList<AssistTool> Offered(bool mayRunCommands, bool mayEditFiles)
    {
        List<AssistTool> offered = [];
        if (mayRunCommands)
            offered.Add(AssistTools.FleetRunner([.. _hosts.Select(host => host.Alias)]));
        if (Here is not null)
        {
            // Reading needs no switch: opening the folder was the decision.
            offered.AddRange(WorkspaceTools.ReadingLocal);
            if (mayEditFiles)
                offered.Add(WorkspaceTools.LocalWriter);
        }
        if (tools is { } connected)
            offered.AddRange(connected.Offered);
        return offered;
    }

    private string System(bool mayEditFiles) => Here switch
    {
        { } here when mayEditFiles =>
            string.Join("\n\n", AssistPrompts.Fleet, AssistPrompts.LocalWorkspace(here.RootLabel, true)),
        { } here => string.Join("\n\n",
            AssistPrompts.Fleet,
            AssistPrompts.LocalWorkspace(here.RootLabel, false),
            AssistPrompts.FleetCannotWriteHere(folderOpen: true)),
        null => string.Join("\n\n", AssistPrompts.Fleet, AssistPrompts.FleetCannotWriteHere(folderOpen: false)),
    };

    /// <summary>A file on this machine was written by the assistant.</summary>
    public event EventHandler<FileChange>? WroteHere;

    /// <summary>
    /// What the fan-out is told when it calls a tool it was not given.
    ///
    /// A host's own files get a sentence of their own, with somewhere to go:
    /// they are never offered here, by design, and a model that asks for one is
    /// usually trying to do something the pane about that host does properly.
    /// Everything else is told what the pane about one host would say.
    /// </summary>
    private static AssistToolResult Unoffered(AssistToolCall call) =>
        WorkspaceTools.Owns(call.Name) && !WorkspaceTools.IsLocal(call.Name)
            ? new AssistToolResult(
                call.Id,
                "Files on the hosts are not available in a fleet run. Ask about one host on its own to "
                    + "read or change a file there.",
                Failed: true)
            : HostAgent.Unoffered(call);

    /// <summary>
    /// One call to the folder on this machine.
    ///
    /// The same <see cref="WorkspaceCalls"/> the per-host conversation uses:
    /// the policy, the budget, the gate and the diff it shows have one
    /// implementation, whichever assistant asked.
    /// </summary>
    private async Task<(bool Ran, AssistToolResult Result)> CarryFile(
        AssistToolCall call,
        ICommandBudget budget,
        CancellationToken cancellationToken)
    {
        if (Here is not { } here)
        {
            return (false, new AssistToolResult(
                call.Id,
                "No folder is open on the user's own machine, so there is nothing to read or write there.",
                Failed: true));
        }

        var calls = new WorkspaceCalls(
            here,
            gate,
            "this machine",
            Append,
            entry => Updated?.Invoke(this, entry),
            secrets: _secrets);

        calls.Wrote += (_, change) => WroteHere?.Invoke(this, change);

        return await calls.Carry(call, budget, cancellationToken);
    }

    /// <summary>One call: on which host, judged, maybe asked about, maybe run.</summary>
    /// <param name="throttle">Shared by reads running side by side, or null for a call that runs alone.</param>
    private async Task<(bool Ran, AssistToolResult Result)> Carry(
        AssistToolCall call,
        ICommandBudget budget,
        CancellationToken cancellationToken,
        SemaphoreSlim? throttle = null)
    {
        if (tools is { } connected && connected.Owns(call.Name))
            return (false, new AssistToolResult(call.Id, "Connected tools are not available in a fleet run.", Failed: true));

        if (WorkspaceTools.Owns(call.Name))
            return await CarryFile(call, budget, cancellationToken);

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

        if (throttle is not null && !throttle.Wait(0))
        {
            step.State = StepState.Queued;
            Updated?.Invoke(this, step);
            await throttle.WaitAsync(cancellationToken);
        }

        step.State = StepState.Running;
        Updated?.Invoke(this, step);
        Working?.Invoke(this, new AssistStep(alias, command, why, Running: true));

        CommandOutcome outcome;
        try
        {
            outcome = await access.Run(command, AssistLimits.CommandTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Working?.Invoke(this, new AssistStep(alias, command, why, Running: false));
            throw;
        }
        catch (Exception e)
        {
            step.State = StepState.Failed;
            step.Output = e.Message;
            Updated?.Invoke(this, step);
            Working?.Invoke(this, new AssistStep(alias, command, why, Running: false));
            return (false, new AssistToolResult(call.Id, $"The command could not be run: {e.Message}", Failed: true));
        }
        finally
        {
            // Its place goes to the next read as soon as the host has answered.
            throttle?.Release();
        }

        var output = HostAgent.Truncate(Redaction.Scrub(outcome.Output, _secrets).Text);
        step.State = outcome.TimedOut ? StepState.TimedOut : StepState.Ran;
        step.ExitStatus = outcome.TimedOut ? null : outcome.ExitStatus;
        step.Output = output;
        Updated?.Invoke(this, step);
        Working?.Invoke(this, new AssistStep(alias, command, why, Running: false, outcome.ExitStatus, output));

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
        // Under the lock, because reads run side by side, and two of them for
        // the same host must find one connection rather than open two.
        lock (_lock)
        {
            if (_opened.TryGetValue(alias, out var held))
                return held;
            return _opened[alias] = _hosts.FirstOrDefault(host => host.Alias == alias)?.Access();
        }
    }

    /// <summary>For what reads running side by side share: the transcript and the opened hosts.</summary>
    private readonly Lock _lock = new();

    private static string Latest(StringBuilder said, StringBuilder thought, string found) =>
        said.Length > 0 ? said.ToString()
        : thought.Length > 0 ? thought.ToString()
        : found;

    private void Append(TranscriptEntry entry)
    {
        lock (_lock)
            _entries.Add(entry);
        Added?.Invoke(this, entry);
    }
}
