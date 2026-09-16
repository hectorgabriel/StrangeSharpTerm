using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// One row of a conversation, as the pane draws it.
///
/// A wrapper rather than the <see cref="TranscriptEntry"/> itself: a step's
/// state changes under it, an answer grows as it streams, and a view binds to
/// something that says when it did.
/// </summary>
public sealed partial class AssistRow : ObservableObject
{
    public required TranscriptEntry Entry { get; init; }

    public bool IsQuestion => Entry is TranscriptEntry.Question;

    public bool IsAnswer => Entry is TranscriptEntry.Answer;

    public bool IsStep => Entry is TranscriptEntry.Step;

    public bool IsNote => Entry is TranscriptEntry.Note;

    public string Text => Entry switch
    {
        TranscriptEntry.Question question => question.Text,
        TranscriptEntry.Answer answer => answer.Markdown,
        TranscriptEntry.Note note => note.Text,
        _ => "",
    };

    /// <summary>Summarised reasoning, where the provider offers it.</summary>
    public string Reasoning => Entry is TranscriptEntry.Answer answer ? answer.Reasoning : "";

    /// <summary>
    /// Whether there is reasoning to offer at all.
    ///
    /// It used to disappear the moment the answer started, which made it a
    /// loading indicator rather than something to read: by the time you noticed
    /// it had said something worth keeping, it was gone. It stays, folded once
    /// the answer arrives.
    /// </summary>
    public bool HasReasoning => Reasoning.Length > 0;

    /// <summary>Open while it is all there is, folded once the answer is on screen.</summary>
    public bool ShowsReasoning => HasReasoning && (IsReasoningOpen || Text.Length == 0);

    [ObservableProperty]
    public partial bool IsReasoningOpen { get; set; }

    public string ReasoningToggle => ShowsReasoning ? "hide" : "show";

    [RelayCommand]
    public void ToggleReasoning() => IsReasoningOpen = !IsReasoningOpen;

    partial void OnIsReasoningOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsReasoning));
        OnPropertyChanged(nameof(ReasoningToggle));
    }

    /// <summary>
    /// The answer as prose and fenced blocks, each drawn as what it is.
    ///
    /// Rendering the markdown raw would put the fence characters on screen, and
    /// a code block would read as part of the sentence above it.
    /// </summary>
    public IReadOnlyList<AnswerBlock> Blocks =>
        Entry is TranscriptEntry.Answer answer ? Fences.Parse(answer.Markdown) : [];

    /// <summary>
    /// The blocks that get a button: only the ones the model tagged as shell.
    /// Nothing else in an answer gets one.
    /// </summary>
    public IReadOnlyList<AnswerBlock.Code> Staged =>
        Entry is TranscriptEntry.Answer answer ? Fences.ShellBlocks(answer.Markdown) : [];

    public TranscriptEntry.Step? Step => Entry as TranscriptEntry.Step;

    public string Command => Step?.Command ?? "";

    public string Why => Step?.Why ?? "";

    public string Output => Step?.Output ?? "";

    public bool HasOutput => Output.Length > 0;

    /// <summary>
    /// What the row says on the right. "auto" is the important one: it means
    /// the policy recognised the command and nobody was asked.
    /// </summary>
    public string State => Step switch
    {
        null => "",
        { State: StepState.Waiting } => "waiting for you",
        { State: StepState.Running } => "running",
        { State: StepState.Refused } => "refused",
        { State: StepState.TimedOut } => "gave up waiting",
        { State: StepState.Failed } => "failed",
        { State: StepState.Skipped } => "budget spent",
        { RanUnattended: true } => "auto",
        _ => "allowed",
    };

    public bool IsWaiting => Step is { State: StepState.Waiting };

    /// <summary>Why a person is being asked. Empty when the policy let it through.</summary>
    public string Gate => Step?.Gate ?? "";

    public bool IsDestructive => Step?.IsDestructive ?? false;

    /// <summary>Redraws the row. The agent mutates the entry; this says when.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// A conversation about one host: the pane the assistant lives in.
///
/// It splits beside the session it is about, so the terminal stays visible while
/// the question is being asked, and a suggested command is typed into that
/// terminal rather than run.
/// </summary>
public sealed partial class AssistantViewModel : ObservableObject, ICommandGate, IDisposable
{
    private readonly HostAgent _agent;
    private readonly Action<string> _stage;
    // By reference, not by value. A step is a record whose state changes as it
    // runs, so its hash changes with it: keyed by value, the row for a command
    // becomes unfindable the moment the command finishes, and every step stays
    // on screen saying "running" forever.
    private readonly Dictionary<TranscriptEntry, AssistRow> _rows =
        new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _asking;
    private TaskCompletionSource<bool>? _answering;
    private TaskCompletionSource<ToolApproval>? _answeringTool;

    /// <param name="stage">
    /// How a command reaches the terminal. It <em>types</em> it and stops: no
    /// newline, no execution. The person reads it and presses Return.
    /// </param>
    public AssistantViewModel(HostAgent agent, AssistSettings settings, Action<string> stage)
    {
        _agent = agent;
        _stage = stage;
        MayRunCommands = settings.AllowCommandsByDefault;

        _agent.Added += (_, entry) => Post(() =>
        {
            var row = new AssistRow { Entry = entry };
            _rows[entry] = row;
            Rows.Add(row);
            OnPropertyChanged(nameof(IsEmpty));
        });
        _agent.Updated += (_, entry) => Post(() =>
        {
            if (_rows.TryGetValue(entry, out var row))
                row.Refresh();
            OnPropertyChanged(nameof(Waiting));
        });
    }

    public string Alias => _agent.Alias;

    /// <summary>
    /// Whether the header names the host itself.
    ///
    /// Docked it does not: the dock has a header of its own that names the host,
    /// and at the width a third column can afford, the two labels and the
    /// provider between them leave the name a few characters wide. Split beside
    /// a terminal there is nothing else to say it, so it stays.
    /// </summary>
    public bool ShowsAlias { get; init; } = true;

    /// <summary>
    /// Which provider and model is answering, in the header.
    ///
    /// It decides where this conversation's terminal output is being sent, and
    /// that should be readable without opening Settings.
    /// </summary>
    public string Answering => $"{_agent.ProviderName} · {_agent.Model}";

    public ObservableCollection<AssistRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    [ObservableProperty]
    public partial string Question { get; set; } = "";

    /// <summary>
    /// Whether the model may run commands itself. Off unless the user turns it
    /// on here, for this conversation — not for the app.
    /// </summary>
    [ObservableProperty]
    public partial bool MayRunCommands { get; set; }

    [ObservableProperty]
    public partial bool IsAsking { get; private set; }

    /// <summary>What the next question will carry, exactly as it will be sent.</summary>
    [ObservableProperty]
    public partial string Preview { get; private set; } = "";

    /// <summary>How many secrets the redactor took out of the preview.</summary>
    [ObservableProperty]
    public partial int Redactions { get; private set; }

    public string RedactionNote => Redactions switch
    {
        0 => "Nothing was removed.",
        1 => "1 secret removed.",
        _ => $"{Redactions} secrets removed.",
    };

    /// <summary>The command a person is being asked about, or null.</summary>
    public AssistRow? Waiting => Rows.FirstOrDefault(row => row.IsWaiting);

    /// <summary>
    /// A connected tool's call, waiting on a person.
    ///
    /// Shown in the bar itself with the server, the tool, where it goes and the
    /// exact arguments: a gate whose substance is one click away is a gate
    /// people approve without reading.
    /// </summary>
    [ObservableProperty]
    public partial PendingToolCall? WaitingTool { get; private set; }

    public bool CanAsk => !IsAsking && Question.Trim().Length > 0;

    partial void OnQuestionChanged(string value) => AskCommand.NotifyCanExecuteChanged();

    partial void OnIsAskingChanged(bool value) => AskCommand.NotifyCanExecuteChanged();

    partial void OnRedactionsChanged(int value) => OnPropertyChanged(nameof(RedactionNote));

    /// <summary>
    /// Fills the disclosure with what a question would carry.
    ///
    /// The preview is the rendered block itself rather than a description of it,
    /// so it cannot drift from the request.
    /// </summary>
    [RelayCommand]
    public async Task Look()
    {
        try
        {
            var context = await _agent.Context();
            Preview = context.Render();
            Redactions = context.Redactions;
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"gathering context for {Alias} failed: {e.Message}");
            Preview = "The host could not be asked what it is doing just now.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanAsk))]
    public async Task Ask()
    {
        var question = Question.Trim();
        if (question.Length == 0 || IsAsking)
            return;

        Question = "";
        IsAsking = true;
        _asking = new CancellationTokenSource();

        try
        {
            await _agent.Ask(question, new AskOptions { MayRunCommands = MayRunCommands }, _asking.Token);
            await Look();
        }
        finally
        {
            IsAsking = false;
            _asking?.Dispose();
            _asking = null;
        }
    }

    /// <summary>Stops the loop. What it already ran is already run; what it has not asked for, it will not.</summary>
    [RelayCommand]
    public void Stop()
    {
        // A pending approval has to be answered, or the loop waits on a
        // question nobody will now see.
        _answering?.TrySetResult(false);
        _answeringTool?.TrySetResult(ToolApproval.No);
        _asking?.Cancel();
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

    /// <summary>
    /// Stops asking about this tool.
    ///
    /// The only way a standing pass is ever granted, which is why it is a
    /// separate button and not a checkbox somebody leaves ticked.
    /// </summary>
    [RelayCommand]
    public void AlwaysAllow() => _answeringTool?.TrySetResult(ToolApproval.Always);

    /// <summary>
    /// Types a suggested command into the terminal and stops.
    ///
    /// No newline: the irreversible step stays a deliberate one, and a model's
    /// suggestion has no claim to more trust than a server's host key does.
    /// </summary>
    [RelayCommand]
    public void Stage(AnswerBlock.Code? block)
    {
        if (block is not null)
            _stage(block.Staged);
    }

    /// <summary>
    /// The gate, answered in the pane rather than in a dialog.
    ///
    /// The command is already a row in the transcript by the time this is
    /// called, so what the person is answering is in front of them with its
    /// reason beside it.
    /// </summary>
    public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default)
    {
        _answering = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => OnPropertyChanged(nameof(Waiting)));
        cancellationToken.Register(() => _answering?.TrySetResult(false));
        return _answering.Task;
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
        _asking?.Cancel();
        _asking?.Dispose();
    }

    /// <summary>
    /// The agent runs off the UI thread and raises its events there. Everything
    /// that touches a bound collection comes back first.
    /// </summary>
    private static void Post(Action work)
    {
        if (Dispatcher.UIThread.CheckAccess())
            work();
        else
            Dispatcher.UIThread.Post(work);
    }
}
