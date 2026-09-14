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
public sealed record FindingRow(HostFinding Finding)
{
    public string Alias => Finding.Alias;

    public string Text => Finding.Text;

    public string Label => Finding.Label;

    public bool Reported => Finding.Outcome == HostOutcome.Reported;

    public bool NotAsked => Finding.Outcome == HostOutcome.NotAsked;
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
    private readonly Orchestrator _orchestrator;

    /// <summary>Where each ticked host's row belongs, for the run in progress.</summary>
    private Dictionary<string, int> _order = new(StringComparer.Ordinal);

    private CancellationTokenSource? _running;
    private TaskCompletionSource<bool>? _answering;
    private TaskCompletionSource<ToolApproval>? _answeringTool;

    public OrchestratorViewModel(IAssistBackend backend, IEnumerable<TargetRow> targets, Func<string, HostAgent?> agentFor)
    {
        _backend = backend;
        _agentFor = agentFor;
        // One of each, for the life of the pane rather than the life of a run:
        // the conversation is the point of them.
        _planner = new Planner(backend);
        _planner.Thought += (_, thought) => Post(() => Thinking = thought);
        _orchestrator = new Orchestrator(backend);
        _orchestrator.Thought += (_, thought) => Post(() => Thinking = thought);
        _orchestrator.Reported += (_, finding) => Post(() => Place(finding));
        foreach (var target in targets)
        {
            target.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Chosen));
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

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>The collated answer, once every host has reported.</summary>
    [ObservableProperty]
    public partial string Collated { get; private set; } = "";

    /// <summary>
    /// The instruction the findings below are answers to.
    ///
    /// Kept and shown rather than left in the field: the field is where the next
    /// one is typed, and a page of host findings with no question above them is
    /// a page of answers to nothing.
    /// </summary>
    [ObservableProperty]
    public partial string Asked { get; private set; } = "";

    /// <summary>Why a plan was refused, naming the phase. Refused rather than shown.</summary>
    [ObservableProperty]
    public partial string? Refusal { get; private set; }

    /// <summary>A command waiting on a person, captioned with its host.</summary>
    [ObservableProperty]
    public partial PendingCommand? Waiting { get; private set; }

    /// <summary>A connected tool's call, waiting on a person, captioned with the host that asked.</summary>
    [ObservableProperty]
    public partial PendingToolCall? WaitingTool { get; private set; }

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

    public bool HasPlan => Phases.Count > 0;

    public bool CanRun => !IsRunning && Chosen.Count > 0 && Instruction.Trim().Length > 0;

    /// <summary>What the button says: writing a plan is not running one.</summary>
    public string RunLabel => IsPlanning ? (HasPlan ? "Run the plan" : "Plan it") : "Run";

    partial void OnModeChanged(OrchestratorMode value)
    {
        OnPropertyChanged(nameof(IsAsking));
        OnPropertyChanged(nameof(IsPlanning));
        OnPropertyChanged(nameof(ModeNote));
        OnPropertyChanged(nameof(RunLabel));
    }

    partial void OnInstructionChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    public void Choose(OrchestratorMode mode) => Mode = mode;

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task Run()
    {
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
    private async Task Fan()
    {
        Findings.Clear();
        Collated = "";
        Refusal = null;
        var instruction = Instruction.Trim();
        Asked = instruction;

        // The order the hosts were ticked in, not the order they answer in. A
        // run across a rack is read as a list, and a list that reorders itself
        // as each host finishes is one nobody can follow -- least of all on the
        // second reading, when it comes out differently.
        _order = Chosen
            .Select((alias, index) => (alias, index))
            .ToDictionary(ticked => ticked.alias, ticked => ticked.index, StringComparer.Ordinal);

        Thinking = "";
        IsThinkingOpen = true;

        await Working(async token =>
        {
            var run = await _orchestrator.Ask(instruction, Ticked(), MayRunCommands, token);
            Post(() =>
            {
                Collated = run.Collated;
                Progress = $"{run.HostsAsked} hosts · {run.CommandsRun} commands";
                IsThinkingOpen = false;
            });
        });
    }

    /// <summary>
    /// Where a finding goes: after every finding whose host was ticked before
    /// it, whatever order they happen to answer in.
    /// </summary>
    private void Place(HostFinding finding)
    {
        var mine = _order.GetValueOrDefault(finding.Alias, int.MaxValue);
        var at = Findings.Count(placed => _order.GetValueOrDefault(placed.Alias, int.MaxValue) < mine);
        Findings.Insert(at, new FindingRow(finding));
        Progress = $"{Findings.Count} of {_order.Count} reported";
    }

    /// <summary>Asks for a plan and runs none of it.</summary>
    private async Task Draft()
    {
        Phases.Clear();
        Findings.Clear();
        Refusal = null;
        Collated = "";
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
            row.Findings.Clear();
            (row.Outcome, row.Captured, row.Note) = ("", null, null);
        }

        var runner = new PlanRunner(AgentFor);
        runner.Finished += (_, result) => Post(() =>
        {
            if (Phases.FirstOrDefault(row => row.Phase == result.Phase) is not { } row)
                return;

            row.Outcome = Describe(result.Outcome);
            row.Captured = result.Captured;
            row.Note = result.Note;
            foreach (var finding in result.Findings)
                row.Findings.Add(new FindingRow(finding));
        });

        await Working(async token =>
        {
            var plan = new RunPlan([.. Phases.Select(row => row.Phase)]);
            var result = await runner.Run(plan, MayRunCommands, token);
            Post(() => Progress = result.Stopped ? result.StoppedBecause ?? "The run stopped." : "Finished.");
        });
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
        _answering?.TrySetResult(false);
        _answeringTool?.TrySetResult(ToolApproval.No);
        _running?.Cancel();
    }

    [RelayCommand]
    public void Allow()
    {
        _answering?.TrySetResult(true);
        _answeringTool?.TrySetResult(ToolApproval.Once);
    }

    [RelayCommand]
    public void Refuse()
    {
        _answering?.TrySetResult(false);
        _answeringTool?.TrySetResult(ToolApproval.No);
    }

    /// <inheritdoc cref="AssistantViewModel.AlwaysAllow"/>
    [RelayCommand]
    public void AlwaysAllow() => _answeringTool?.TrySetResult(ToolApproval.Always);

    /// <summary>
    /// The gate, per host and saying which one.
    ///
    /// Approving <c>systemctl restart nginx</c> means nothing until you know
    /// whose nginx, so every waiting command is captioned with its host.
    /// </summary>
    public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default)
    {
        _answering = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _answering;
        Post(() => Waiting = command);
        cancellationToken.Register(() => pending.TrySetResult(false));
        return pending.Task.ContinueWith(
            answered =>
            {
                Post(() => Waiting = null);
                return answered.Result;
            },
            TaskScheduler.Default);
    }

    /// <inheritdoc cref="Allow(PendingCommand, CancellationToken)"/>
    public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default)
    {
        var answering = new TaskCompletionSource<ToolApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answeringTool = answering;
        Post(() => WaitingTool = call);
        cancellationToken.Register(() => answering.TrySetResult(ToolApproval.No));

        return answering.Task.ContinueWith(
            answered =>
            {
                Post(() => WaitingTool = null);
                return answered.Result;
            },
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        _answering?.TrySetResult(false);
        _answeringTool?.TrySetResult(ToolApproval.No);
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

    private IReadOnlyList<OrchestratorTarget> Ticked() =>
    [
        .. Targets
            .Where(target => target.IsChosen)
            .Select(target => new OrchestratorTarget(
                target.Alias,
                // Still only built for a host that is actually asked, so a run
                // over eight hosts does not open eight connections it will not
                // use -- but built once and kept, so the host remembers.
                () => AgentFor(target.Alias)
                    ?? throw new AssistException($"{target.Alias} is not in the inventory."))),
    ];

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
