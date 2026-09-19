using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>One host, tickable, with whether it could actually be asked.</summary>
public sealed partial class TargetRow : ObservableObject
{
    public required string Alias { get; init; }

    /// <summary>
    /// Connecting can raise a host-key decision, and a fan-out that stopped on a
    /// dialog for every host would be worse than one that says plainly which
    /// hosts it left out. So a disconnected host stays tickable and is reported
    /// as not asked.
    /// </summary>
    public required bool IsConnected { get; init; }

    [ObservableProperty]
    public partial bool IsChosen { get; set; }
}

/// <summary>What one host reported, as its row draws it.</summary>
/// <summary>
/// One host's part in a run: the row that names it, what it ended up saying, and
/// the exchange that got there.
///
/// The row exists from the moment the host is asked rather than from the moment
/// it answers, because a phase can take minutes and a list that stays empty
/// until it is over is a list that looks broken. The exchange underneath is the
/// conversation itself -- what it was asked, what it was thinking, every command
/// it ran and what came back -- which until now was visible only for a host you
/// had opened an assistant pane on.
/// </summary>
public sealed partial class FindingRow : ObservableObject, IDisposable
{
    private HostAgent? _watched;

    public FindingRow(string alias) => Alias = alias;

    public FindingRow(HostFinding finding)
        : this(finding.Alias) => Finding = finding;

    public string Alias { get; }

    /// <summary>What it ended up saying, or null while it is still being asked.</summary>
    [ObservableProperty]
    public partial HostFinding? Finding { get; set; }

    public string Text => Finding?.Text ?? "";

    public string Label => Finding?.Label ?? "working";

    public bool Reported => Finding?.Outcome == HostOutcome.Reported;

    public bool NotAsked => Finding?.Outcome == HostOutcome.NotAsked;

    /// <summary>
    /// Something went wrong on this host, which is the one outcome that wants
    /// a colour.
    ///
    /// A failed host used to be the same small grey word as one that reported,
    /// in the same place -- and this list exists precisely so that a summary
    /// cannot read as though it covered hosts it never reached.
    /// </summary>
    public bool Failed => Finding?.Outcome == HostOutcome.Failed;

    /// <summary>Interrupted rather than broken: worth marking, but not as alarming.</summary>
    public bool Stopped => Finding?.Outcome == HostOutcome.Stopped;

    /// <summary>The conversation with this host, as it happens.</summary>
    public ObservableCollection<AssistRow> Exchange { get; } = [];

    public bool HasExchange => Exchange.Count > 0;

    [ObservableProperty]
    public partial bool IsExchangeOpen { get; set; }

    public string ExchangeToggle => IsExchangeOpen ? "hide exchange" : "show exchange";

    [RelayCommand]
    public void ToggleExchange() => IsExchangeOpen = !IsExchangeOpen;

    /// <summary>
    /// Follows this host's agent from now on.
    ///
    /// From now rather than from the beginning: the agent is kept for the life
    /// of the pane, so its transcript holds every phase and every earlier run,
    /// and this row is about one of them.
    /// </summary>
    public void Watch(HostAgent agent)
    {
        if (_watched is not null)
            return;
        _watched = agent;
        agent.Added += OnAdded;
        agent.Updated += OnUpdated;
    }

    public void Dispose()
    {
        if (_watched is null)
            return;
        _watched.Added -= OnAdded;
        _watched.Updated -= OnUpdated;
        _watched = null;
    }

    partial void OnFindingChanged(HostFinding? value)
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Reported));
        OnPropertyChanged(nameof(NotAsked));
        OnPropertyChanged(nameof(Failed));
        OnPropertyChanged(nameof(Stopped));
    }

    partial void OnIsExchangeOpenChanged(bool value) => OnPropertyChanged(nameof(ExchangeToggle));

    private void OnAdded(object? sender, TranscriptEntry entry) => Post(() =>
    {
        Exchange.Add(new AssistRow { Entry = entry });
        OnPropertyChanged(nameof(HasExchange));
    });

    private void OnUpdated(object? sender, TranscriptEntry entry) => Post(() =>
    {
        if (Exchange.FirstOrDefault(row => row.Entry == entry) is { } row)
            row.Refresh();
    });

    private static void Post(Action work)
    {
        if (Dispatcher.UIThread.CheckAccess())
            work();
        else
            Dispatcher.UIThread.Post(work);
    }
}

/// <summary>A phase of a plan, as the pane draws it and lets it be switched off.</summary>
public sealed partial class PhaseRow(PlanPhase phase, int number) : ObservableObject
{
    public PlanPhase Phase { get; } = phase;

    public string Number => $"{number}.";

    public string Name => Phase.Name;

    public string Hosts => string.Join(", ", Phase.Hosts);

    /// <summary>Why this phase is on these hosts, where the planner said.</summary>
    public string? Why => Phase.Why;

    public bool Explains => Why is { Length: > 0 };

    /// <summary>
    /// The commands themselves, one per line, exactly as they will be sent.
    ///
    /// Not summarised and not counted: a plan is worth reviewing only to the
    /// extent the thing reviewed is the thing that runs.
    /// </summary>
    public string Commands => string.Join("\n", Phase.Commands);

    public bool Yields => Phase.Capture is { Length: > 0 };

    public string YieldsWhat => $"yields {Phase.Capture}";

    public bool Uses => Phase.Placeholders.Count > 0;

    public string UsesWhat => $"uses {string.Join(", ", Phase.Placeholders)}";

    /// <summary>Phases can be switched off, which is what makes reviewing one worth anything.</summary>
    public bool IsEnabled
    {
        get => Phase.IsEnabled;
        set
        {
            Phase.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>What happened to it, once the plan has run.</summary>
    [ObservableProperty]
    public partial string Outcome { get; set; } = "";

    /// <summary>The captured value, shown verbatim where it was produced.</summary>
    [ObservableProperty]
    public partial string? Captured { get; set; }

    public string CapturedLabel => $"{Phase.Capture} =";

    [ObservableProperty]
    public partial string? Note { get; set; }

    public ObservableCollection<FindingRow> Findings { get; } = [];

    partial void OnCapturedChanged(string? value) => OnPropertyChanged(nameof(CapturedLabel));
}

/// <summary>Which of the two things the pane is doing.</summary>
public enum OrchestratorMode
{
    /// <summary>One question, every selected host at once.</summary>
    Ask,

    /// <summary>Work in phases, in order — reviewed before it runs.</summary>
    Plan,
}

/// <summary>
/// One instruction across several hosts.
///
/// The pane belongs to no single connection, which is why its <see cref="Pane"/>
/// has no host: disconnecting a host closes the panes belonging to it, and a
/// fan-out across eight servers should not vanish because one was disconnected.
/// </summary>
public sealed partial class OrchestratorViewModel : ObservableObject, ICommandGate, IDisposable
{
    private readonly Func<string, HostAgent?> _agentFor;

    private readonly Func<string, IHostAccess?> _accessFor;

    /// <summary>
    /// A host reached through the agent that already has one.
    ///
    /// Only for callers that supplied an agent and no access of their own: the
    /// window supplies both, because Ask mode wants the connection without the
    /// conversation that used to come with it.
    /// </summary>
    private sealed class Named(HostAgent agent) : IHostAccess
    {
        public string Alias => agent.Alias;

        public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostSnapshot());

        public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("this orchestrator was given no way to run commands");
    }
    private readonly IAssistBackend _backend;

    /// <summary>
    /// One agent per host, for as long as this pane is open.
    ///
    /// A fresh one was built for every phase, so on a given host phase three had
    /// never heard of phase one -- the only thing that crossed between them was
    /// the single captured value. Holding them means each host remembers its own
    /// work, which is what makes a plan a sequence rather than three unrelated
    /// errands.
    /// </summary>
    private readonly Dictionary<string, HostAgent> _agents = new(StringComparer.Ordinal);

    private readonly Planner _planner;

    private CancellationTokenSource? _running;

    /// <summary>
    /// Everyone waiting on a person, oldest first.
    ///
    /// A queue rather than one pending answer, because a fan-out runs three
    /// hosts at once and all three can reach the gate together. Holding one
    /// meant the second to arrive overwrote the first, which then waited on an
    /// answer nobody could give — and because the caption was set separately
    /// from the answer, the banner could name one host while the buttons
    /// answered for another.
    ///
    /// Guarded by <see cref="_asking"/>: it is added to from whichever worker
    /// thread a host is running on, and taken from on the UI thread.
    /// </summary>
    private readonly List<Asking> _waiting = [];

    private readonly Lock _asking = new();

    /// <param name="agentFor">
    /// A conversation about one host, for plan mode, which still runs a phase's
    /// commands through a per-host agent.
    /// </param>
    /// <param name="accessFor">
    /// A way to run a command on one host, for Ask mode, which has one
    /// conversation and needs no agent per host at all -- only the connection.
    /// </param>
    /// <param name="localWorkspace">
    /// The folder open on the machine this app is running on, asked for each
    /// time rather than captured: it is opened and closed while the pane is.
    /// </param>
    public OrchestratorViewModel(
        IAssistBackend backend,
        IEnumerable<TargetRow> targets,
        Func<string, HostAgent?> agentFor,
        Func<string, IHostAccess?>? accessFor = null,
        IWorkspaceAccess? localWorkspace = null,
        Func<string?>? localRoot = null,
        Func<string>? connectedTools = null)
    {
        _localWorkspace = localWorkspace;
        _localRoot = localRoot;
        _connectedTools = connectedTools;
        // Falls back to the agent's own host, so a test that only cares about
        // one of the two modes need only supply that one.
        _accessFor = accessFor ?? (alias => agentFor(alias) is { } agent ? new Named(agent) : null);
        _backend = backend;
        _agentFor = agentFor;
        // One of each, for the life of the pane rather than the life of a run:
        // the conversation is the point of them.
        // The same folder as Ask mode, read-only: a plan is often written from a
        // runbook, and the runbook is here.
        _planner = new Planner(backend, localWorkspace);
        _planner.Thought += (_, thought) => Post(() => Thinking = thought);
        // What it is reading, while it reads it. Draft replaces this with the
        // plan's summary when the plan arrives.
        _planner.Looked += (_, step) => Post(() => Progress = step.State switch
        {
            StepState.Refused => $"{step.Command} — refused: {step.Gate}",
            StepState.Failed => $"{step.Command} — {step.Output}",
            _ => step.Command,
        });
        foreach (var target in targets)
        {
            // Everything that reads the ticks, not just the list of them.
            // Notifying Chosen alone left the count beside the Hosts header
            // reading "none selected" with two of them ticked, and the Run
            // button disabled until the instruction was touched again -- so
            // ticking a host last, which is the obvious order, gave you a
            // button that did nothing.
            target.PropertyChanged += (_, _) => Chose();
            Targets.Add(target);
        }
    }

    public ObservableCollection<TargetRow> Targets { get; } = [];

    public ObservableCollection<FindingRow> Findings { get; } = [];

    public ObservableCollection<PhaseRow> Phases { get; } = [];

    public string Answering => $"{_backend.ProviderName} · {_backend.Model}";

    [ObservableProperty]
    public partial OrchestratorMode Mode { get; set; } = OrchestratorMode.Ask;

    public bool IsAsking => Mode == OrchestratorMode.Ask;

    public bool IsPlanning => Mode == OrchestratorMode.Plan;

    /// <summary>What each mode does, said where the choice is made.</summary>
    public string ModeNote => IsAsking
        ? "one question, every selected host at once"
        : "commands in phases, in order — read before any of them run";

    [ObservableProperty]
    public partial string Instruction { get; set; } = "";

    [ObservableProperty]
    public partial bool MayRunCommands { get; set; }

    /// <summary>
    /// Whether it may write to the folder open on this machine.
    ///
    /// This machine's and not the hosts': a run is where one instruction becomes
    /// an action on eight servers, and changing a file on one of them stays a
    /// job for the pane about that server. What this is for is the other
    /// direction -- reading a runbook here, and writing what a run found
    /// somewhere that is not a chat window.
    /// </summary>
    [ObservableProperty]
    public partial bool MayEditFiles { get; set; }

    /// <summary>The folder open on this machine, for the switch to name. Empty when there is none.</summary>
    public string LocalRoot => _localRoot?.Invoke() ?? "";

    public bool HasLocalFiles => LocalRoot.Length > 0;

    private Func<string?>? _localRoot;

    /// <summary>Says the folder changed, so the switch and its label catch up.</summary>
    public void LocalFilesChanged()
    {
        OnPropertyChanged(nameof(LocalRoot));
        OnPropertyChanged(nameof(HasLocalFiles));
    }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>
    /// The instruction the findings below are answers to.
    ///
    /// Kept and shown rather than left in the field: the field is where the next
    /// one is typed, and a page of host findings with no question above them is
    /// a page of answers to nothing.
    /// </summary>
    [ObservableProperty]
    public partial string Asked { get; private set; } = "";

    partial void OnAskedChanged(string value) => OnPropertyChanged(nameof(ShowsGoal));

    /// <summary>Why a plan was refused, naming the phase. Refused rather than shown.</summary>
    [ObservableProperty]
    public partial string? Refusal { get; private set; }

    /// <summary>A command waiting on a person, captioned with its host.</summary>
    [ObservableProperty]
    public partial PendingCommand? Waiting { get; private set; }

    /// <summary>A connected tool's call, waiting on a person, captioned with the host that asked.</summary>
    [ObservableProperty]
    public partial PendingToolCall? WaitingTool { get; private set; }

    /// <summary>
    /// How many other hosts are queued behind this one, said where the decision
    /// is made.
    ///
    /// A run stops three hosts at once, and answering what is in front of you
    /// without knowing two more are coming is how a person clicks through the
    /// third without reading it.
    /// </summary>
    [ObservableProperty]
    public partial string WaitingMore { get; private set; } = "";

    [ObservableProperty]
    public partial string Progress { get; private set; } = "";

    /// <summary>
    /// The model's reasoning, where the provider offers it.
    ///
    /// Kept rather than replaced by the answer: the plan says which host was
    /// chosen and this says why, which is the half a reviewer needs and the half
    /// that used to be thrown away. Collapsed once there is something to read,
    /// because it is long and the plan is the point.
    /// </summary>
    [ObservableProperty]
    public partial string Thinking { get; private set; } = "";

    public bool HasThinking => Thinking.Length > 0;

    /// <summary>
    /// What the orchestrator already remembers, said where the next instruction
    /// is typed.
    ///
    /// The screen shows one plan at a time, so without this there is nothing to
    /// tell you whether "put it on the other one" will be understood as a change
    /// to the last plan or read cold.
    /// </summary>
    public string Continuing => _planner.Turns switch
    {
        0 => "",
        1 => "continuing from 1 earlier plan",
        var turns => $"continuing from {turns} earlier plans",
    };

    public bool IsContinuing => _planner.Turns > 0;

    /// <summary>Open while it is the only thing there is, and foldable afterwards.</summary>
    [ObservableProperty]
    public partial bool IsThinkingOpen { get; set; } = true;

    partial void OnThinkingChanged(string value) => OnPropertyChanged(nameof(HasThinking));

    partial void OnIsThinkingOpenChanged(bool value) => OnPropertyChanged(nameof(ThinkingToggle));

    public string ThinkingToggle => IsThinkingOpen ? "hide" : "show";

    [RelayCommand]
    public void ToggleThinking() => IsThinkingOpen = !IsThinkingOpen;

    public IReadOnlyList<string> Chosen => [.. Targets.Where(target => target.IsChosen).Select(target => target.Alias)];

    public string ChosenNote => Chosen.Count switch
    {
        0 => "none selected",
        1 => "1 host",
        _ => $"{Chosen.Count} hosts",
    };

    /// <summary>
    /// Whether the goal is shown above everything. Only in plan mode: asking
    /// keeps its questions in the conversation, and this would repeat the last.
    /// </summary>
    public bool ShowsGoal => IsPlanning && Asked.Length > 0;

    public bool HasPlan => Phases.Count > 0;

    /// <summary>
    /// Whether the button does anything.
    ///
    /// A command is exempt from needing hosts. /help with nothing ticked is a
    /// perfectly sensible thing to type -- it is how you find out what you can
    /// type -- and a button that stayed grey for it would look broken rather
    /// than strict.
    /// </summary>
    public bool CanRun =>
        !IsRunning
        && Instruction.Trim().Length > 0
        && (Chosen.Count > 0 || ChatCommands.Looks(Instruction));

    /// <summary>What the button says: writing a plan is not running one.</summary>
    public string RunLabel => IsPlanning ? (HasPlan ? "Run the plan" : "Plan it") : "Run";

    partial void OnModeChanged(OrchestratorMode value)
    {
        OnPropertyChanged(nameof(IsAsking));
        OnPropertyChanged(nameof(IsPlanning));
        OnPropertyChanged(nameof(ModeNote));
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(ShowsGoal));
    }

    partial void OnInstructionChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    /// <summary>The ticks changed, and with them everything derived from them.</summary>
    private void Chose()
    {
        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(ChosenNote));
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    public void Choose(OrchestratorMode mode) => Mode = mode;

    /// <summary>What <c>/mcp</c> answers with. Supplied by the window, which owns the servers.</summary>
    private Func<string>? _connectedTools;

    /// <inheritdoc cref="AssistantViewModel.Handle"/>
    public bool Handle(string text)
    {
        if (!ChatCommands.Looks(text))
            return false;

        var fleet = _fleet ??= Fleet();
        switch (ChatCommands.Name(text))
        {
            case ChatCommands.Clear:
                fleet.Clear();
                break;

            case ChatCommands.Mcp:
                fleet.Say(_connectedTools?.Invoke() ?? Assistant.ConnectedToolsReport.Of(null));
                break;

            case ChatCommands.Help:
                fleet.Say(ChatCommands.Listing);
                break;

            default:
                fleet.Say(ChatCommands.Unknown(ChatCommands.Name(text)));
                break;
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task Run()
    {
        // A command is not an instruction, and must not become a run across
        // eight hosts because it started with a slash.
        if (Handle(Instruction.Trim()))
        {
            Instruction = "";
            return;
        }

        if (IsPlanning && !HasPlan)
        {
            await Draft();
            return;
        }

        if (IsPlanning)
        {
            await Carry();
            return;
        }

        await Fan();
    }

    /// <summary>One question, every selected host, then one call to collate them.</summary>
    /// <summary>
    /// One assistant, asked once, reaching whichever of the ticked hosts it
    /// decides to.
    ///
    /// It used to open a conversation per host and a further one to collate
    /// what they each reported. This is one, and it chooses where to look: the
    /// rows below are its transcript, with the host named on every command,
    /// rather than a card per host with a summary underneath.
    /// </summary>
    private async Task Fan()
    {
        // The rows are not cleared and the assistant is not rebuilt. A second
        // instruction is a second turn: it can refer to what the last one
        // found, and the pane shows both.
        Refusal = null;
        var instruction = Instruction.Trim();
        Asked = instruction;

        // Emptied, because the question is in the transcript now and the next
        // one is a different question. A plan keeps its goal in the box -- that
        // is what Run presses on a second time -- but asking never re-sends.
        Instruction = "";

        Thinking = "";
        IsThinkingOpen = true;

        var fleet = _fleet ??= Fleet();
        var hosts = Ticked();
        await Working(async token =>
        {
            var answer = await fleet.Ask(instruction, hosts, MayRunCommands, MayEditFiles, token);
            Post(() =>
            {
                Progress = $"{Chosen.Count} hosts · {answer.CommandsRun} commands";
                IsThinkingOpen = false;
            });

            // The planner was not there at all, and a plan asked for next is
            // about these hosts in the state this run left them.
            _planner.Record($"# Asked across {Chosen.Count} hosts: {instruction}\n\n{answer.Text}");
        });
    }

    /// <summary>
    /// The hosts ticked now, which is what this question is about. A later one
    /// may be about a different set, and reaches the same conversation.
    /// </summary>
    private IReadOnlyList<FleetHost> Ticked() =>
    [
        .. Targets.Where(target => target.IsChosen)
            .Select(target => new FleetHost(target.Alias, () => _accessFor(target.Alias))),
    ];

    /// <summary>
    /// The fleet assistant, built on first use and kept for as long as the pane
    /// is open.
    ///
    /// One per pane rather than one per run: it was one per run, and every
    /// question arrived at an assistant that had never been asked anything.
    /// </summary>
    private FleetAgent? _fleet;

    private readonly IWorkspaceAccess? _localWorkspace;

    /// <summary>A file on this machine was written by the run. The sidebar and the editor listen.</summary>
    public event EventHandler<FileChange>? WroteHere;

    private FleetAgent Fleet()
    {
        var fleet = new FleetAgent(_backend, this, null, _localWorkspace);

        fleet.WroteHere += (_, change) => Post(() => WroteHere?.Invoke(this, change));

        fleet.Cleared += (_, _) => Post(() =>
        {
            _rows.Clear();
            Rows.Clear();
            Asked = "";
            Thinking = "";
            Progress = "";
        });

        fleet.Added += (_, entry) => Post(() =>
        {
            _rows[entry] = new AssistRow { Entry = entry };
            Rows.Add(_rows[entry]);
            if (entry is TranscriptEntry.Answer answer)
                Thinking = answer.Reasoning;
        });
        fleet.Updated += (_, entry) => Post(() =>
        {
            if (_rows.TryGetValue(entry, out var row))
                row.Refresh();
            if (entry is TranscriptEntry.Answer answer && answer.Reasoning.Length > 0)
                Thinking = answer.Reasoning;
        });

        // Which host it has, while it has it. A pane showing that host says so
        // for as long as the command is running.
        fleet.Working += (_, step) => Post(() => Driving?.Invoke(this, step));
        return fleet;
    }

    /// <summary>
    /// What the assistant is doing on which host, for the window to show.
    ///
    /// Raised on the way into a command and again on the way out, so a pane can
    /// say the assistant has this host and then that it has let go.
    /// </summary>
    public event EventHandler<AssistStep>? Driving;

    /// <summary>The fleet conversation, as the pane draws it.</summary>
    public ObservableCollection<AssistRow> Rows { get; } = [];

    // By reference, not by value, for the reason AssistantViewModel's own copy
    // of this records: a step is a record whose state changes as it runs, so
    // its hash changes with it. Keyed by value, the row for a command becomes
    // unfindable the moment the command finishes, and every step stays on
    // screen saying "running" forever -- which is exactly what it did.
    private readonly Dictionary<TranscriptEntry, AssistRow> _rows =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Empties a list of rows and lets go of the agents they were following.
    ///
    /// Clearing alone would leave every row still subscribed, so a second run
    /// would be watched by the rows of the first as well as its own.
    /// </summary>
    private static void Forget(ObservableCollection<FindingRow> rows)
    {
        foreach (var row in rows)
            row.Dispose();
        rows.Clear();
    }

    /// <summary>Asks for a plan and runs none of it.</summary>
    private async Task Draft()
    {
        Phases.Clear();
        Forget(Findings);
        Refusal = null;
        var goal = Instruction.Trim();
        // The goal stands above the plan for the same reason the question stands
        // above the findings: review is the only thing between a model and a
        // fleet, and reviewing a plan means reading it against what was asked.
        Asked = goal;

        Thinking = "";
        IsThinkingOpen = true;

        await Working(async token =>
        {
            var reading = await _planner.Draft(goal, Chosen, token);
            Post(() =>
            {
                switch (reading)
                {
                    case PlanReading.Ok(var plan):
                        foreach (var (phase, number) in plan.Phases.Select((phase, index) => (phase, index + 1)))
                            Phases.Add(new PhaseRow(phase, number));
                        Progress = $"{plan.Summary} · not run yet";
                        // The plan is what to read now. The reasoning stays a
                        // click away rather than pushing it off the screen.
                        IsThinkingOpen = false;
                        break;

                    // A plan that would not be safe to run is refused rather
                    // than shown, and the refusal says which phase and why.
                    case PlanReading.Refused(var reason):
                        Refusal = reason;
                        Progress = "";
                        break;
                }
                OnPropertyChanged(nameof(HasPlan));
                OnPropertyChanged(nameof(RunLabel));
                OnPropertyChanged(nameof(Continuing));
                OnPropertyChanged(nameof(IsContinuing));
            });
        });
    }

    /// <summary>Carries out the plan on screen: phases in order, hosts three at a time.</summary>
    private async Task Carry()
    {
        foreach (var row in Phases)
        {
            Forget(row.Findings);
            (row.Outcome, row.Captured, row.Note) = ("", null, null);
        }

        var runner = new PlanRunner(AgentFor);

        // A row per host the moment the phase is sent, each following its agent
        // from now on -- the agent's transcript spans the whole plan, and this
        // row is about this phase.
        runner.Starting += (_, phase) => Post(() =>
        {
            if (Phases.FirstOrDefault(row => row.Phase == phase) is not { } row)
                return;

            foreach (var alias in phase.Hosts)
            {
                var finding = new FindingRow(alias);
                if (AgentFor(alias) is { } agent)
                    finding.Watch(agent);
                row.Findings.Add(finding);
            }
        });

        runner.Reported += (_, finding) => Post(() =>
        {
            if (Phases.SelectMany(phase => phase.Findings)
                    .LastOrDefault(row => row.Alias == finding.Alias && row.Finding is null) is { } waiting)
            {
                waiting.Finding = finding;
            }
        });

        runner.Finished += (_, result) => Post(() =>
        {
            if (Phases.FirstOrDefault(row => row.Phase == result.Phase) is not { } row)
                return;

            row.Outcome = Describe(result.Outcome);
            row.Captured = result.Captured;
            row.Note = result.Note;

            // Anything that never came through Reported -- a phase that was
            // skipped whole, so no host was ever asked.
            foreach (var finding in result.Findings.Where(finding =>
                row.Findings.All(placed => placed.Alias != finding.Alias || placed.Finding is null)))
            {
                if (row.Findings.FirstOrDefault(placed => placed.Alias == finding.Alias) is { } waiting)
                    waiting.Finding = finding;
                else
                    row.Findings.Add(new FindingRow(finding));
            }
        });

        await Working(async token =>
        {
            var plan = new RunPlan([.. Phases.Select(row => row.Phase)]);
            var result = await runner.Run(plan, MayRunCommands, token);
            Post(() =>
            {
                Progress = result.Stopped ? result.StoppedBecause ?? "The run stopped." : "Finished.";
                OnPropertyChanged(nameof(Continuing));
                OnPropertyChanged(nameof(IsContinuing));
            });

            // What the hosts reported goes back to the assistant that planned
            // it. Without this the plan is written, the hosts carry it out, and
            // the next question is answered by the one participant that never
            // found out whether any of it worked.
            var reported = Reported(result);
            _planner.Record(reported);
        });
    }

    /// <summary>
    /// What a run amounts to, in the shape the summariser already reads: each
    /// phase, what became of it, and what each of its hosts said.
    /// </summary>
    private static string Reported(PlanRunResult run)
    {
        List<string> lines = [];
        foreach (var phase in run.Phases)
        {
            lines.Add($"# {phase.Phase.Name} ({Describe(phase.Outcome)})");
            if (phase.Note is { Length: > 0 } note)
                lines.Add(note);
            if (phase is { CapturedName: { Length: > 0 } name, Captured: { Length: > 0 } value })
                lines.Add($"{name} = {value}");
            foreach (var finding in phase.Findings)
            {
                lines.Add($"## {finding.Alias} ({finding.Label})");
                lines.Add(finding.Text);
            }
            lines.Add("");
        }

        if (run.Stopped && run.StoppedBecause is { Length: > 0 } because)
            lines.Add($"The run stopped: {because}");

        // The same string on either operating system, as the context block is.
        return string.Join('\n', lines).Replace("\r", "").Trim();
    }

    private static string Describe(PhaseOutcome outcome) => outcome switch
    {
        PhaseOutcome.Completed => "done",
        PhaseOutcome.Disabled => "switched off",
        PhaseOutcome.Stalled => "no host completed it",
        PhaseOutcome.NothingCaptured => "produced no value",
        PhaseOutcome.Unfilled => "skipped",
        _ => "not reached",
    };

    /// <summary>Throws the plan away and starts again. Nothing it already ran is undone.</summary>
    [RelayCommand]
    public void Discard()
    {
        Phases.Clear();
        Refusal = null;
        Progress = "";
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(RunLabel));
    }

    [RelayCommand]
    public void Stop()
    {
        // Everyone, not just whoever is on screen: a host left waiting would
        // hold its place in the run until the process ended.
        AnswerEveryone(ToolApproval.No);
        _running?.Cancel();
    }

    [RelayCommand]
    public void Allow() => Answer(ToolApproval.Once);

    [RelayCommand]
    public void Refuse() => Answer(ToolApproval.No);

    /// <inheritdoc cref="AssistantViewModel.AlwaysAllow"/>
    [RelayCommand]
    public void AlwaysAllow() => Answer(ToolApproval.Always);

    /// <summary>
    /// The gate, per host and saying which one.
    ///
    /// Approving <c>systemctl restart nginx</c> means nothing until you know
    /// whose nginx, so every waiting command is captioned with its host.
    /// </summary>
    public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default)
    {
        var asking = Join(new Asking { Command = command }, cancellationToken);
        return asking.Answered.Task.ContinueWith(
            answered => answered.Result != ToolApproval.No,
            TaskScheduler.Default);
    }

    /// <inheritdoc cref="Allow(PendingCommand, CancellationToken)"/>
    public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default) =>
        Join(new Asking { Tool = call }, cancellationToken).Answered.Task;

    /// <summary>
    /// One host waiting on a person: what it wants, and the answer it is blocked
    /// on. A command and a tool call queue together because they arrive together
    /// — from different hosts, at the same moment, in the same run.
    /// </summary>
    private sealed class Asking
    {
        public PendingCommand? Command { get; init; }

        public PendingToolCall? Tool { get; init; }

        public TaskCompletionSource<ToolApproval> Answered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Let go of the token, or a long run accumulates one of these per gated command.</summary>
        public CancellationTokenRegistration Cancelling { get; set; }
    }

    /// <summary>Takes a place in the queue, and shows it if the queue was empty.</summary>
    private Asking Join(Asking asking, CancellationToken cancellationToken)
    {
        lock (_asking)
            _waiting.Add(asking);

        // Registered after joining, so a token that is already cancelled finds
        // it there to remove rather than leaving it behind for ever.
        asking.Cancelling = cancellationToken.Register(() => Settle(asking, ToolApproval.No));

        Post(Show);
        return asking;
    }

    /// <summary>Answers whoever is on screen, and brings the next one up.</summary>
    private void Answer(ToolApproval approval)
    {
        Asking? asking;
        lock (_asking)
            asking = _waiting.FirstOrDefault();

        if (asking is not null)
            Settle(asking, approval);
    }

    private void AnswerEveryone(ToolApproval approval)
    {
        Asking[] everyone;
        lock (_asking)
            everyone = [.. _waiting];

        foreach (var asking in everyone)
            Settle(asking, approval);
    }

    /// <summary>
    /// Gives one host its answer and takes it out of the queue.
    ///
    /// Removed before the answer is set: the host wakes on another thread and
    /// may be back at the gate immediately, and it must not find itself still
    /// queued from last time.
    /// </summary>
    private void Settle(Asking asking, ToolApproval approval)
    {
        lock (_asking)
        {
            if (!_waiting.Remove(asking))
                return;
        }

        asking.Cancelling.Dispose();
        asking.Answered.TrySetResult(approval);
        Post(Show);
    }

    /// <summary>Puts the head of the queue in the banners, and nothing if it is empty.</summary>
    private void Show()
    {
        Asking? head;
        int behind;
        lock (_asking)
        {
            head = _waiting.FirstOrDefault();
            behind = Math.Max(0, _waiting.Count - 1);
        }

        // Both set from the one place, so the caption and the buttons can never
        // be about different hosts.
        Waiting = head?.Command;
        WaitingTool = head?.Tool;
        WaitingMore = behind switch
        {
            0 => "",
            1 => "1 more waiting",
            _ => $"{behind} more waiting",
        };
    }

    public void Dispose()
    {
        // Closing the pane is the same promise Stop makes: a run may be waiting
        // on an approval nobody will now see, and every one of them is answered
        // rather than the one that happened to be showing.
        AnswerEveryone(ToolApproval.No);
        _running?.Cancel();
        _running?.Dispose();
    }

    /// <summary>This host's agent, built on first use and kept for the life of the pane.</summary>
    private HostAgent? AgentFor(string alias)
    {
        if (_agents.TryGetValue(alias, out var held))
            return held;
        if (_agentFor(alias) is not { } built)
            return null;
        return _agents[alias] = built;
    }

    private async Task Working(Func<CancellationToken, Task> work)
    {
        IsRunning = true;
        _running = new CancellationTokenSource();
        try
        {
            await work(_running.Token);
        }
        catch (OperationCanceledException)
        {
            Post(() => Progress = "Stopped.");
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"orchestrated run failed: {e}");
            Post(() => Refusal = e.Message);
        }
        finally
        {
            IsRunning = false;
            _running?.Dispose();
            _running = null;
        }
    }

    private static void Post(Action work)
    {
        if (Dispatcher.UIThread.CheckAccess())
            work();
        else
            Dispatcher.UIThread.Post(work);
    }
}
