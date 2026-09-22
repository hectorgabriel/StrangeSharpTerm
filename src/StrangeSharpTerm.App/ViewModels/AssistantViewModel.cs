using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Assistant;
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

    /// <summary>
    /// Which host the command went to.
    ///
    /// Always known, and only worth drawing where a conversation covers more
    /// than one machine: in a pane about a single host it would be the same
    /// word on every row.
    /// </summary>
    public string Host => Step?.Host ?? "";

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

    /// <summary>
    /// The lines a write would change, shown with the question rather than
    /// behind it: approving a write you have not read is approving a file you
    /// have not read.
    /// </summary>
    public string Detail => Step?.Detail ?? "";

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Whether this row is a file in the workspace rather than a command on the host.</summary>
    public bool IsFile => Step?.IsFile ?? false;

    /// <summary>
    /// What the button says. "Run it" is wrong for a file: nothing runs, a file
    /// is replaced, and a button that says the wrong verb is a button people
    /// press for the wrong reason.
    /// </summary>
    public string AllowLabel => IsFile ? "Write it" : "Run it";

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
    /// <param name="workspace">
    /// What folder this host has open, asked for each time rather than captured:
    /// a docked conversation outlives the pane it was opened from, and a folder
    /// can be opened after the conversation has started.
    /// </param>
    public AssistantViewModel(
        HostAgent agent,
        AssistSettings settings,
        Action<string> stage,
        Func<string?>? workspace = null,
        Func<string?>? localWorkspace = null,
        Func<string>? connectedTools = null)
    {
        _agent = agent;
        _stage = stage;
        _workspace = workspace;
        _localWorkspace = localWorkspace;
        _connectedTools = connectedTools;
        MayRunCommands = settings.AllowCommandsByDefault;

        _agent.Added += (_, entry) => _screen.Now(() =>
        {
            var row = new AssistRow { Entry = entry };
            _rows[entry] = row;
            Rows.Add(row);
            OnPropertyChanged(nameof(IsEmpty));
        });
        // Spaced rather than drawn as it arrives, and keyed on the entry so what
        // is waiting is the newest state of it. A refresh redraws the whole
        // answer -- markdown re-parsed, thinking re-wrapped -- and a reasoning
        // model sends a token at a time. See <see cref="Streamed"/>.
        _agent.Updated += (_, entry) => _screen.Soon(entry, () =>
        {
            if (_rows.TryGetValue(entry, out var row))
                row.Refresh();
            OnPropertyChanged(nameof(Waiting));
        });
        // Which host it has, while it has it. The pane showing this host says
        // so for as long as the command is running, exactly as it does for an
        // orchestrated run -- the window cannot tell the two apart and should
        // not have to.
        _agent.Working += (_, step) => _screen.Now(() => Driving?.Invoke(this, step));
        // A file it wrote is a file the workspace pane is still showing the old
        // version of. The window joins the two; this only says when.
        _agent.Wrote += (_, change) => _screen.Now(() => Wrote?.Invoke(this, change));
        _agent.WroteHere += (_, change) => _screen.Now(() => WroteHere?.Invoke(this, change));
        // A retry takes the last question back, and the rows it produced go
        // with it -- or the pane would show both attempts as though both had
        // been asked.
        _agent.Removed += (_, entry) => _screen.Now(() =>
        {
            if (!_rows.Remove(entry, out var row))
                return;
            Rows.Remove(row);
            OnPropertyChanged(nameof(IsEmpty));
        });
        _agent.Cleared += (_, _) => _screen.Now(() =>
        {
            _rows.Clear();
            Rows.Clear();
            _asked.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            RetryCommand.NotifyCanExecuteChanged();
        });
    }

    /// <summary>
    /// What the assistant is doing on this host, for the window to show.
    ///
    /// Raised on the way into a command and again on the way out, so a pane can
    /// say the assistant has this host and then that it has let go.
    /// </summary>
    public event EventHandler<AssistStep>? Driving;

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

    /// <summary>
    /// Whether it may write to the folder this host has open.
    ///
    /// Its own switch beside the commands one, because they are different
    /// decisions about different things: a command runs and is over, and a file
    /// is still changed tomorrow. Reading the folder is not behind either — that
    /// was decided by opening it.
    /// </summary>
    [ObservableProperty]
    public partial bool MayEditFiles { get; set; }

    /// <summary>
    /// Whether the folder switch is worth showing at all. There is nothing to
    /// edit until a folder is open, and a toggle for nothing is a toggle that
    /// teaches people it does nothing.
    /// </summary>
    public bool HasWorkspace =>
        _workspace?.Invoke() is { Length: > 0 } || _localWorkspace?.Invoke() is { Length: > 0 };

    private readonly Func<string?>? _workspace;

    private readonly Func<string?>? _localWorkspace;

    /// <summary>The folder open on this host, for the header to name. Empty when there is none.</summary>
    public string WorkspaceRoot
    {
        get
        {
            var host = _workspace?.Invoke() ?? "";
            var here = _localWorkspace?.Invoke() ?? "";
            return (host.Length, here.Length) switch
            {
                (> 0, > 0) => $"{host} on this host, and {here} on this machine",
                (> 0, _) => host,
                (_, > 0) => $"{here} on this machine",
                _ => "",
            };
        }
    }

    /// <summary>Says the folder changed, so the header and the switch catch up.</summary>
    public void WorkspaceChanged()
    {
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(WorkspaceRoot));
    }

    /// <summary>A file in the open folder was written by the assistant.</summary>
    public event EventHandler<FileChange>? Wrote;

    /// <inheritdoc cref="Wrote"/>
    public event EventHandler<FileChange>? WroteHere;

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

    partial void OnIsAskingChanged(bool value)
    {
        AskCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The last answer as it was written, for the button that copies it.</summary>
    [RelayCommand]
    public void CopyAnswer(AssistRow? row)
    {
        if (row?.Text is { Length: > 0 } text)
            Copied?.Invoke(this, text);
    }

    /// <summary>
    /// Something wants to go on the clipboard.
    ///
    /// An event rather than a call, because reaching the clipboard needs a
    /// visual to find the window from, and a view model that held one would be
    /// a view model that could not be tested without one.
    /// </summary>
    public event EventHandler<string>? Copied;

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

    /// <summary>What <c>/mcp</c> answers with. Supplied by the window, which owns the servers.</summary>
    private readonly Func<string>? _connectedTools;

    /// <summary>
    /// Handles a line that is a command rather than a question.
    ///
    /// Nothing here leaves the machine: these are questions about the app, and
    /// asking a model about the app it is running inside gets an answer that
    /// sounds right and was checked against nothing.
    /// </summary>
    /// <returns>Whether the line was one, and so must not be sent.</returns>
    public bool Handle(string text)
    {
        if (!ChatCommands.Looks(text))
            return false;

        switch (ChatCommands.Name(text))
        {
            case ChatCommands.Clear:
                _agent.Clear();
                break;

            case ChatCommands.Mcp:
                _agent.Say(_connectedTools?.Invoke() ?? ConnectedToolsReport.Of(null));
                break;

            case ChatCommands.Help:
                _agent.Say(ChatCommands.Listing);
                break;

            default:
                _agent.Say(ChatCommands.Unknown(ChatCommands.Name(text)));
                break;
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(CanAsk))]
    public async Task Ask()
    {
        var question = Question.Trim();
        if (question.Length == 0 || IsAsking)
            return;

        // Before anything is remembered or sent: a command is not a question,
        // and walking back through what you asked should not offer you /clear.
        if (Handle(question))
        {
            Question = "";
            return;
        }

        Question = "";
        Remember(question);
        await Put(question);
    }

    /// <summary>
    /// Asks the last question again, from before it was asked.
    ///
    /// The conversation is wound back first, so this is another attempt rather
    /// than a follow-up: asking again into a conversation that already holds
    /// the question and its answer is asking the model to improve on itself,
    /// which is a different thing and usually not what the button meant.
    ///
    /// What it ran on the host is not untaken. Nothing here reaches a server.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    public async Task Retry()
    {
        if (IsAsking || _asked.Count == 0)
            return;

        var question = _asked[^1];
        _agent.Rewind();
        await Put(question);
    }

    public bool CanRetry => !IsAsking && _agent.CanRewind && _asked.Count > 0;

    private async Task Put(string question)
    {
        IsAsking = true;
        _asking = new CancellationTokenSource();

        try
        {
            await _agent.Ask(
                question,
                new AskOptions { MayRunCommands = MayRunCommands, MayEditFiles = MayEditFiles },
                _asking.Token);
            await Look();
        }
        finally
        {
            IsAsking = false;
            _asking?.Dispose();
            _asking = null;
            RetryCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// What has been asked here, oldest first, for the up arrow to walk back
    /// through.
    ///
    /// The pane's own, not the agent's: the agent's transcript is what was
    /// asked and answered, and this is what was typed -- which includes the
    /// question you are part way through rewriting.
    /// </summary>
    private readonly List<string> _asked = [];

    private int _walked;

    private void Remember(string question)
    {
        // Not twice in a row: asking the same thing again is one entry to walk
        // back to, not two.
        if (_asked.Count == 0 || _asked[^1] != question)
            _asked.Add(question);
        _walked = 0;
    }

    /// <summary>
    /// The question before this one, or the one after it.
    ///
    /// Returns false at either end so the view can leave the caret alone rather
    /// than blanking the field at the bottom of the list.
    /// </summary>
    public bool Recall(int direction)
    {
        if (_asked.Count == 0)
            return false;

        var walked = Math.Clamp(_walked + direction, 0, _asked.Count);
        if (walked == _walked)
            return false;

        _walked = walked;
        Question = walked == 0 ? "" : _asked[^walked];
        return true;
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
        _screen.Now(() => OnPropertyChanged(nameof(Waiting)));
        cancellationToken.Register(() => _answering?.TrySetResult(false));
        return _answering.Task;
    }

    /// <inheritdoc cref="Allow(PendingCommand, CancellationToken)"/>
    public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default)
    {
        var answering = new TaskCompletionSource<ToolApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answeringTool = answering;
        _screen.Now(() => WaitingTool = call);
        cancellationToken.Register(() => answering.TrySetResult(ToolApproval.No));

        return answering.Task.ContinueWith(
            answered =>
            {
                _screen.Now(() => WaitingTool = null);
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
    /// that touches a bound collection comes back first, and what streams comes
    /// back spaced -- see <see cref="Streamed"/>.
    /// </summary>
    private readonly Streamed _screen = new();
}
