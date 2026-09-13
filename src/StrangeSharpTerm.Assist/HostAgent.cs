using System.Text;

namespace StrangeSharpTerm.Assist;

/// <summary>How one question is to be answered.</summary>
public sealed record AskOptions
{
    /// <summary>
    /// Whether the model gets the tool at all. Off is the default everywhere: a
    /// suggested command arrives as a block with a button that types it and
    /// stops, and the person presses Return.
    /// </summary>
    public bool MayRunCommands { get; init; }

    /// <summary>
    /// How many commands this question may cost. A run across several hosts
    /// hands in a budget shared with the rest of the run.
    /// </summary>
    public ICommandBudget? Budget { get; init; }

    public TimeSpan? CommandTimeout { get; init; }

    /// <summary>Something extra for this question alone: a phase's task, or an instruction to report a value.</summary>
    public string? Instruction { get; init; }
}

/// <summary>What a question produced.</summary>
public sealed record AgentAnswer(string Text, int CommandsRun, bool Failed = false, string? Failure = null)
{
    public static AgentAnswer Broken(string failure) => new("", 0, Failed: true, Failure: failure);
}

/// <summary>
/// A conversation about one host, and the loop that carries it out.
///
/// It asks for something, the app runs it or stops to ask, the result goes back,
/// and it decides what to do next. Three bounds hold it: twelve commands per
/// question, sixty seconds each, and output redacted and truncated like
/// everything else before it goes to a provider.
///
/// The same object is what an orchestrated run gives each of its hosts.
/// Delegating rather than adding a host argument to the tool is the whole
/// design: nothing about running a command on a machine gets a second
/// implementation, and each host's investigation stays in its own transcript.
/// </summary>
public sealed class HostAgent(
    IAssistBackend backend,
    IHostAccess host,
    AssistSettings settings,
    ICommandGate gate)
{
    private readonly List<AssistMessage> _conversation = [];
    private readonly List<TranscriptEntry> _entries = [];

    public string Alias => host.Alias;

    public string ProviderName => backend.ProviderName;

    public string Model => backend.Model;

    /// <summary>The conversation as the pane draws it.</summary>
    public IReadOnlyList<TranscriptEntry> Entries => _entries;

    /// <summary>
    /// What the last question carried, exactly as it was sent.
    ///
    /// The pane's disclosure shows this rather than a description of it, so the
    /// preview cannot drift from the request.
    /// </summary>
    public HostContext? LastContext { get; private set; }

    /// <summary>A row was added. The pane appends rather than redrawing the list.</summary>
    public event EventHandler<TranscriptEntry>? Added;

    /// <summary>A row changed underneath: an answer grew, a step finished.</summary>
    public event EventHandler<TranscriptEntry>? Updated;

    /// <summary>
    /// Gathers what a question would carry, without asking anything.
    ///
    /// The pane calls this to fill its disclosure before a question is typed:
    /// every question shows exactly what it will carry before you ask it.
    /// </summary>
    public async Task<HostContext> Context(CancellationToken cancellationToken = default)
    {
        var snapshot = await Look(cancellationToken);
        var tail = Redaction.Scrub(snapshot.TerminalTail);
        return LastContext = new HostContext
        {
            Alias = host.Alias,
            Kernel = snapshot.Kernel,
            Metrics = settings.SendMetrics ? snapshot.Metrics : null,
            TerminalTail = settings.SendTerminalTail && tail.Text.Length > 0 ? tail.Text : null,
            Redactions = settings.SendTerminalTail ? tail.Count : 0,
        };
    }

    /// <summary>Asks one question and runs the loop until the model stops asking for things.</summary>
    public async Task<AgentAnswer> Ask(string question, AskOptions? options = null, CancellationToken cancellationToken = default)
    {
        var how = options ?? new AskOptions();
        var budget = how.Budget ?? new CommandBudget(AssistLimits.CommandBudget);
        var timeout = how.CommandTimeout ?? AssistLimits.CommandTimeout;
        var ran = 0;

        Append(new TranscriptEntry.Question(question));

        var context = await Context(cancellationToken);
        _conversation.Add(new AssistMessage
        {
            Role = AssistRole.User,
            Text = string.Join("\n\n", new[] { context.Render(), how.Instruction, question }
                .Where(part => !string.IsNullOrWhiteSpace(part))),
        });

        // One turn per round trip, and a ceiling well above the command budget:
        // each command is a turn, and the model gets a few more to read the last
        // result and say what it found.
        var ceiling = AssistLimits.CommandBudget + 4;

        for (var turn = 0; turn < ceiling; turn++)
        {
            var answer = new TranscriptEntry.Answer();
            var calls = new List<AssistToolCall>();
            var stop = AssistStop.EndTurn;
            var said = new StringBuilder();
            var thought = new StringBuilder();
            var shown = false;

            try
            {
                var request = new AssistRequest
                {
                    System = how.MayRunCommands ? AssistPrompts.HostWithCommands : AssistPrompts.Host,
                    // Copied, not handed over: the conversation grows during
                    // this turn, and a request that changed underneath the
                    // backend reading it would be a very quiet bug.
                    Messages = [.. _conversation],
                    Tools = how.MayRunCommands ? [AssistTools.Runner] : [],
                };

                await foreach (var streamed in backend.Stream(request, cancellationToken))
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

                        case AssistEvent.Finished finished:
                            stop = finished.Reason;
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Append(new TranscriptEntry.Note("Stopped."));
                return new AgentAnswer(said.ToString(), ran);
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

            if (stop == AssistStop.Refusal)
                Append(new TranscriptEntry.Note($"{backend.ProviderName} declined to answer this one."));
            else if (stop == AssistStop.Length)
                Append(new TranscriptEntry.Note("The answer was cut short; it reached the length limit."));

            if (calls.Count == 0)
                return new AgentAnswer(said.ToString(), ran);

            var results = new List<AssistToolResult>();
            foreach (var call in calls)
            {
                var (outcome, result) = await Carry(call, budget, timeout, cancellationToken);
                if (outcome)
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

        // The ceiling, which the budget should have reached first. Saying so is
        // better than a pane that simply stops.
        Append(new TranscriptEntry.Note("This question went on long enough that the assistant stopped."));
        return new AgentAnswer("", ran);
    }

    /// <summary>
    /// One tool call: judged, maybe asked about, maybe run.
    /// </summary>
    /// <returns>Whether a command actually ran, and what goes back to the model.</returns>
    private async Task<(bool Ran, AssistToolResult Result)> Carry(
        AssistToolCall call,
        ICommandBudget budget,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (call.Name != AssistTools.RunCommand)
        {
            return (false, new AssistToolResult(call.Id, $"There is no tool called {call.Name}.", Failed: true));
        }

        var (command, why) = AssistTools.ReadRun(call.Arguments);
        var judgement = CommandPolicy.Judge(command);
        var step = new TranscriptEntry.Step
        {
            Host = host.Alias,
            Command = command,
            Why = why,
            Gate = judgement.Reason,
            IsDestructive = judgement.IsDestructive,
        };
        Append(step);

        if (!budget.Take())
        {
            step.State = StepState.Skipped;
            Updated?.Invoke(this, step);
            return (false, new AssistToolResult(
                call.Id,
                "The command budget for this question is spent. Do not ask for anything else; "
                    + "summarise what you have found so far.",
                Failed: true));
        }

        if (!judgement.MayRunUnattended)
        {
            var allowed = await gate.Allow(
                new PendingCommand(host.Alias, command, why, judgement.Reason, judgement.IsDestructive),
                cancellationToken);

            if (!allowed)
            {
                step.State = StepState.Refused;
                Updated?.Invoke(this, step);
                // Told plainly, which is what stops it trying the same thing a
                // different way.
                return (false, new AssistToolResult(
                    call.Id,
                    "The user refused to run this command. Do not attempt the same thing another way. "
                        + "Work with what you already have, or say what you would need and why.",
                    Failed: true));
            }
        }

        step.State = StepState.Running;
        Updated?.Invoke(this, step);

        CommandOutcome outcome;
        try
        {
            outcome = await host.Run(command, timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            step.State = StepState.Failed;
            step.Output = e.Message;
            Updated?.Invoke(this, step);
            return (false, new AssistToolResult(call.Id, $"The command could not be run: {e.Message}", Failed: true));
        }

        // Redacted and truncated like everything else before it goes to a
        // provider. What the user sees in the row is the same text.
        var scrubbed = Redaction.Scrub(outcome.Output);
        var output = Truncate(scrubbed.Text);

        step.State = outcome.TimedOut ? StepState.TimedOut : StepState.Ran;
        step.ExitStatus = outcome.TimedOut ? null : outcome.ExitStatus;
        step.Output = output;
        Updated?.Invoke(this, step);

        var reply = outcome.TimedOut
            ? $"The command did not finish within {timeout.TotalSeconds:0} seconds and was left running. "
                + $"Partial output:\n{output}"
            : $"exit status {outcome.ExitStatus}\n{output}";

        return (true, new AssistToolResult(call.Id, reply, Failed: outcome.TimedOut));
    }

    private async Task<HostSnapshot> Look(CancellationToken cancellationToken)
    {
        try
        {
            return await host.Look(
                settings.SendMetrics, settings.SendTerminalTail, settings.TerminalTailLines, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A host that will not answer a probe can still be asked about. The
            // block simply carries less, which the disclosure shows.
            return new HostSnapshot();
        }
    }

    /// <summary>
    /// Keeps both ends of a long output.
    ///
    /// The head says what the command was doing and the tail usually holds the
    /// error; cutting the tail off is how a truncation loses the answer.
    /// </summary>
    internal static string Truncate(string output, int limit = AssistLimits.MaxOutputCharacters)
    {
        if (output.Length <= limit)
            return output;

        var half = limit / 2;
        var removed = output.Length - (half * 2);
        return string.Concat(
            output.AsSpan(0, half),
            $"\n… {removed} characters removed …\n",
            output.AsSpan(output.Length - half));
    }

    private void Append(TranscriptEntry entry)
    {
        _entries.Add(entry);
        Added?.Invoke(this, entry);
    }
}
