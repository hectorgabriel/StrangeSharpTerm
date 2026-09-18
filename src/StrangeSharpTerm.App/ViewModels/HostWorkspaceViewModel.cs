using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// A folder on a server, open: a tree down one side, the files you are editing
/// beside it, and one connection underneath both.
///
/// This is the pane half of the workspace. The other half is that the same root
/// is what the assistant may read and write — <see cref="Workspace"/> is handed
/// to the agent, so the folder you opened is the folder it works in, and closing
/// it takes the tools away again. One root, one rule, two users of it.
///
/// Everything slow happens off the UI thread and every failure lands in
/// <see cref="Failure"/> rather than in an exception, exactly as the file
/// browser does: a pane over a network that throws is a pane that disappears.
/// </summary>
public sealed partial class HostWorkspaceViewModel : ObservableObject
{
    private readonly IRemoteFiles _files;
    private readonly IDialogService _dialogs;
    private readonly Func<Action, Task> _run;
    private readonly Action<string>? _remember;

    /// <param name="offThread">
    /// How work leaves the UI thread. Replaced in tests by something that runs
    /// it there and now, so a test does not have to wait for a thread pool.
    /// </param>
    /// <param name="remember">
    /// Told whenever the root changes, so the app can put it in
    /// <c>preferences.json</c>. A view model that wrote the file itself would be
    /// one that could not be built without one.
    /// </param>
    /// <param name="isLocal">
    /// Whether the folder is on this machine rather than on a server.
    ///
    /// It changes no rule -- the root is the permission either way, and the
    /// gate is the same gate -- and it changes every sentence: "download to this
    /// machine" is meaningless when the file is already on it, and a person
    /// approving a write needs to know which machine it lands on.
    /// </param>
    public HostWorkspaceViewModel(
        IRemoteFiles files,
        string host,
        string? root = null,
        IDialogService? dialogs = null,
        Func<Action, Task>? offThread = null,
        Action<string>? remember = null,
        bool isLocal = false)
    {
        _files = files;
        Host = host;
        IsLocal = isLocal;
        _dialogs = dialogs ?? new ScriptedDialogService();
        _run = offThread ?? (work => Task.Run(work));
        _remember = remember;
        // No folder until somebody names one, where this machine is concerned.
        // Defaulting to the account's own directory would be a browser's habit
        // and the wrong one here: the root is the whole of what this app and the
        // assistant may touch, and nobody chose their entire home directory.
        // A host's folder still defaults to the account there, which is where a
        // connection already puts you.
        Workspace = root is { Length: > 0 } chosen ? new RemoteWorkspace(files, chosen)
            : isLocal ? null
            : new RemoteWorkspace(files, files.Home);
        RootDraft = Workspace?.RootLabel ?? "";
    }

    public string Host { get; }

    /// <summary>Whether this folder is on this machine. See the constructor.</summary>
    public bool IsLocal { get; }

    /// <summary>Where a write lands, for anything that has to name it.</summary>
    public string Where => IsLocal ? "this machine" : Host;

    public string UploadTip => IsLocal ? "Copy a file in…" : $"Upload into this folder on {Host}…";

    public string DownloadTip => IsLocal ? "Copy elsewhere…" : "Download to this machine…";

    public string SaveTip => IsLocal ? "Write it back" : $"Write it back to {Host}";

    /// <summary>
    /// What the path box says when you hover it: the whole root, because the
    /// box itself is 240 pixels wide and a project path is not.
    /// </summary>
    public string RootTip => Root.Length == 0
        ? "The folder this workspace is rooted at"
        : $"{Root} — the only folder the assistant may touch on {Where}";

    /// <summary>
    /// What the empty editor says, which is also where the one rule worth
    /// knowing is written down.
    /// </summary>
    public string EmptyEditorNote => IsLocal
        ? "Open a file from the sidebar. The assistant can read and write anything in this folder on this machine, and nothing outside it."
        : "Open a file from the tree. The assistant can read and write anything in this folder, and nothing outside it.";

    /// <summary>
    /// Sending a local file to the folder a host has open.
    ///
    /// The one thing the two workspaces do together, and the reason the local
    /// one is worth having beside the remote one rather than instead of it:
    /// "get this file onto that server" is the sentence, and it used to need a
    /// file picker and a guess at the destination.
    /// </summary>
    public Func<WorkspaceNode, Task>? Sender { get; set; }

    /// <summary>What the host's folder is called, for the menu item to name it. Empty when none is open.</summary>
    [ObservableProperty]
    public partial string SendTarget { get; set; } = "";

    public bool CanSend => IsLocal && SendTarget.Length > 0 && Selected is { IsDirectory: false };

    public string SendTip => SendTarget.Length > 0 ? $"Send to {SendTarget}" : "Send to the open host";

    partial void OnSendTargetChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SendTip));
    }

    [RelayCommand]
    public async Task Send()
    {
        if (Selected is { IsDirectory: false } node && Sender is { } send)
            await send(node);
    }

    /// <summary>
    /// The folder itself, which is also what the assistant is given.
    ///
    /// Replaced rather than mutated when the root changes, so the agent's next
    /// call is judged against the new root and everything already in flight
    /// against the old one — which is the honest answer, because that call was
    /// approved against the folder that was open when it was asked.
    /// </summary>
    public RemoteWorkspace? Workspace { get; private set; }

    /// <summary>Whether a folder has been chosen at all. Only ever false for this machine's.</summary>
    public bool HasRoot => Workspace is not null;

    public string Root => Workspace?.RootLabel ?? "";

    /// <summary>What is typed into the root bar, which is not the root until Enter.</summary>
    [ObservableProperty]
    public partial string RootDraft { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>What went wrong, in the pane, where the user was looking.</summary>
    [ObservableProperty]
    public partial string? Failure { get; private set; }

    /// <summary>What just happened, when it is worth saying: a file saved, a file uploaded.</summary>
    [ObservableProperty]
    public partial string? Note { get; private set; }

    /// <summary>The root's own children. The root itself is the header, not a row.</summary>
    public ObservableCollection<WorkspaceNode> Tree { get; } = [];

    /// <summary>The files being edited, in the order they were opened.</summary>
    public ObservableCollection<FileEditorViewModel> Open { get; } = [];

    [ObservableProperty]
    public partial FileEditorViewModel? Current { get; set; }

    private FileEditorViewModel? _watched;

    partial void OnCurrentChanged(FileEditorViewModel? value)
    {
        // Whether there is anything to save changes as somebody types, not only
        // when they open something else -- and ⌘S lives on the window, which
        // has no other way to find out.
        if (_watched is not null)
        {
            _watched.PropertyChanged -= Typed;
            _watched.IsCurrent = false;
        }
        _watched = value;
        if (_watched is not null)
        {
            _watched.PropertyChanged += Typed;
            _watched.IsCurrent = true;
        }

        OnPropertyChanged(nameof(HasOpenFile));
        SaveCommand.NotifyCanExecuteChanged();
        Saveability?.Invoke(this, EventArgs.Empty);
    }

    private void Typed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(FileEditorViewModel.IsDirty) or nameof(FileEditorViewModel.Text)))
            return;
        SaveCommand.NotifyCanExecuteChanged();
        Saveability?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>There is now something to save, or there is not. ⌘S asks the window, so the window has to know.</summary>
    public event EventHandler? Saveability;

    public bool HasOpenFile => Current is not null;

    [ObservableProperty]
    public partial WorkspaceNode? Selected { get; set; }

    partial void OnSelectedChanged(WorkspaceNode? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanSend));
        DownloadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
    }

    public bool HasSelection => Selected is not null;

    /// <summary>
    /// Does something slow, somewhere else, and keeps what happened.
    ///
    /// One place to be busy and one place for a failure to land, as the browser
    /// has: every call here can lose an argument with a network.
    /// </summary>
    private async Task Do(string description, Action work)
    {
        IsBusy = true;
        Failure = null;
        try
        {
            await _run(work);
        }
        catch (Exception e)
        {
            Failure = $"{description}: {Explain(e)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Something slow that belongs to this folder but was asked for from
    /// outside it: sending a file to a host, so far.
    ///
    /// It goes through the same busy flag, the same banner and the same note as
    /// everything else here, because to whoever is watching the pane it is the
    /// same kind of event.
    /// </summary>
    public async Task Copying(string description, Action work, string note)
    {
        await Do(description, work);
        if (Failure is null)
            Note = note;
    }

    /// <summary>Reads the root again, keeping whichever folders are open on screen open.</summary>
    [RelayCommand]
    public async Task Refresh()
    {
        // Nothing to do until a folder has been chosen, which on this machine
        // is a deliberate act rather than a default.
        if (Workspace is not { } folder)
            return;

        var expanded = Tree.SelectMany(node => node.Reachable())
            .Where(node => node.IsExpanded)
            .Select(node => node.Relative)
            .ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<RemoteEntry> entries = [];
        await Do($"Reading {Root}", () => entries = folder.List("."));
        if (Failure is not null)
            return;

        Tree.Clear();
        foreach (var node in entries.Select(Node))
            Tree.Add(node);

        // Put back what was open, which is what makes this a refresh rather
        // than a reset: a save that collapsed the tree would lose your place
        // every time you pressed ⌘S.
        foreach (var node in Tree.ToArray())
            await Reopen(node, expanded);
    }

    private async Task Reopen(WorkspaceNode node, HashSet<string> expanded)
    {
        if (!node.IsDirectory || !expanded.Contains(node.Relative))
            return;

        node.IsExpanded = true;
        await node.Fill();
        foreach (var child in node.Children.ToArray())
            await Reopen(child, expanded);
    }

    private WorkspaceNode Node(RemoteEntry entry) =>
        new(entry, Workspace?.Relative(entry.Path) ?? entry.Path, Fill);

    /// <summary>Reads one directory into the row that was expanded.</summary>
    private async Task Fill(WorkspaceNode node)
    {
        // Nothing to do until a folder has been chosen, which on this machine
        // is a deliberate act rather than a default.
        if (Workspace is not { } folder)
            return;

        IReadOnlyList<RemoteEntry> entries = [];
        await Do($"Reading {node.Relative}", () => entries = folder.List(node.Relative));
        if (Failure is not null)
        {
            // An unreadable directory stops looking openable rather than
            // keeping a placeholder row that says "…" forever.
            node.Replace([]);
            return;
        }
        node.Replace(entries.Select(Node));
    }

    /// <summary>
    /// Points the workspace somewhere else, which is what "open folder" means.
    ///
    /// The open editors go with it. They belong to the folder — keeping a file
    /// open from a root that is no longer open would be a file the assistant
    /// cannot see and the tree cannot show, and saving it would write outside
    /// the folder somebody has since chosen.
    /// </summary>
    [RelayCommand]
    public async Task OpenRoot()
    {
        var wanted = RootDraft.Trim();
        if (wanted.Length == 0)
            return;

        // ~ is what the bar shows, so it has to be what the bar accepts.
        var absolute = wanted switch
        {
            "~" => _files.Home,
            _ when wanted.StartsWith("~/") => PosixPath.Join(_files.Home, wanted[2..]),
            _ => wanted,
        };

        var candidate = new RemoteWorkspace(_files, absolute);
        var first = Workspace is null;
        var exists = false;
        await Do($"Opening {wanted}", () => exists = candidate.Stat(".") is { IsDirectory: true });
        if (Failure is not null)
            return;

        if (!exists)
        {
            Failure = $"{wanted} is not a directory on {Where}.";
            return;
        }

        if (!await CloseEverything())
            return;

        Workspace = candidate;
        RootDraft = candidate.RootLabel;
        OnPropertyChanged(nameof(Root));
        if (first)
            OnPropertyChanged(nameof(HasRoot));
        _remember?.Invoke(candidate.Root);
        RootChanged?.Invoke(this, candidate.Root);
        Note = null;
        await Refresh();
    }

    /// <summary>The folder changed. The window listens, to remember it and to tell the assistant.</summary>
    public event EventHandler<string>? RootChanged;

    /// <summary>
    /// Asks for a folder, and opens it.
    ///
    /// The first thing this machine's side of the sidebar offers, because until
    /// somebody answers it there is deliberately nothing open. A picker rather
    /// than a typed path for the first one: choosing what an assistant may read
    /// is not a moment to be guessing at spelling.
    /// </summary>
    [RelayCommand]
    public async Task ChooseFolder()
    {
        if (await _dialogs.PickFolder("Choose a folder to work in") is not { Length: > 0 } chosen)
            return;

        RootDraft = chosen;
        await OpenRoot();
    }

    /// <summary>Opens a file in an editor, or a directory in the tree.</summary>
    [RelayCommand]
    public async Task Activate(WorkspaceNode? node)
    {
        if (node is null || node.IsPlaceholder)
            return;

        if (node.IsDirectory)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        await OpenFile(node.Relative);
    }

    /// <summary>
    /// Opens one file, or brings it forward if it is already open.
    ///
    /// Already open wins over reading it again: the second is what somebody
    /// typed, and a click in the tree is not a reason to throw it away.
    /// </summary>
    public async Task OpenFile(string relative)
    {
        // Nothing to do until a folder has been chosen, which on this machine
        // is a deliberate act rather than a default.
        if (Workspace is not { } folder)
            return;

        if (Open.FirstOrDefault(editor => editor.Relative == relative) is { } already)
        {
            Current = already;
            return;
        }

        FileText? read = null;
        await Do($"Opening {relative}", () => read = folder.Read(relative));
        if (Failure is not null || read is null)
            return;

        if (read.IsBinary)
        {
            // Said rather than shown. A JPEG in a text box is a screenful of
            // replacement characters and a save that would corrupt the file.
            // Nothing to suggest where the file is already here: "download it"
            // is advice about a server, and this one is on the desk.
            Failure = IsLocal
                ? $"{read.Relative} is not a text file."
                : $"{read.Relative} is not a text file. Download it instead.";
            return;
        }

        var editor = new FileEditorViewModel(read);
        Open.Add(editor);
        Current = editor;
        Note = read.Truncated
            ? $"Only the first {RemoteWorkspace.MaxFileBytes / 1000} kB of {read.Relative} is shown, so it cannot be saved from here."
            : null;
    }

    /// <summary>Brings an open file forward. What clicking its tab does.</summary>
    [RelayCommand]
    public void Show(FileEditorViewModel? editor)
    {
        if (editor is not null)
            Current = editor;
    }

    /// <summary>Closes one file, asking first if it has unsaved work in it.</summary>
    [RelayCommand]
    public async Task Close(FileEditorViewModel? editor)
    {
        if (editor is null)
            return;

        if (editor.IsDirty
            && !await _dialogs.Confirm(
                $"Close {editor.Title} without saving?",
                "What you typed has not been written to the server, and closing loses it.",
                "Close"))
        {
            return;
        }

        var index = Open.IndexOf(editor);
        Open.Remove(editor);
        Current = Open.Count == 0 ? null : Open[Math.Min(index, Open.Count - 1)];
    }

    private async Task<bool> CloseEverything()
    {
        foreach (var editor in Open.ToArray())
        {
            await Close(editor);
            if (Open.Contains(editor))
                return false;
        }
        return true;
    }

    public bool CanSave => Current is { IsDirty: true, IsTruncated: false };

    /// <summary>
    /// Writes the open file back, refusing to overwrite what somebody else did
    /// without saying so first.
    ///
    /// The terminal beside this pane is on the same machine. Editing a file
    /// there and saving it here is not a strange case; it is Tuesday.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task Save()
    {
        if (Current is not { } editor || Workspace is not { } folder)
            return;

        if (editor.IsTruncated)
        {
            Failure = $"{editor.Relative} is too large to have been read whole, so saving it would truncate it.";
            return;
        }

        RemoteEntry? now = null;
        await Do($"Checking {editor.Relative}", () => now = folder.Stat(editor.Path));
        if (Failure is not null)
            return;

        if (now is not null && now.Modified > editor.Modified)
        {
            var when = now.Modified.ToString("HH:mm:ss");
            if (!await _dialogs.Confirm(
                    $"{editor.Title} changed on {Where}",
                    $"Somebody or something wrote to it at {when}, after you opened it. Saving replaces that.",
                    "Save anyway"))
            {
                return;
            }
        }

        await Do(
            $"Saving {editor.Relative}",
            () => folder.Write(editor.Relative, editor.Text, editor.Newline, editor.HasByteOrderMark));
        if (Failure is not null)
            return;

        RemoteEntry? written = null;
        await Do($"Checking {editor.Relative}", () => written = folder.Stat(editor.Path));
        editor.Saved(written?.Modified ?? DateTime.UtcNow);
        SaveCommand.NotifyCanExecuteChanged();
        Note = $"Saved {editor.Relative}";
        await Refresh();
    }

    /// <summary>Uploads whatever the user picks, into the selected directory or the root.</summary>
    [RelayCommand]
    public async Task Upload()
    {
        // Nothing to do until a folder has been chosen, which on this machine
        // is a deliberate act rather than a default.
        if (Workspace is not { } folder)
            return;

        var into = Directory();
        var chosen = await _dialogs.PickFiles($"Upload to {(into.Length == 0 ? Root : into)}");
        if (chosen.Count == 0)
            return;

        await Do(
            "Uploading",
            () =>
            {
                foreach (var local in chosen)
                    folder.Upload(local, PosixPath.Join(into, System.IO.Path.GetFileName(local)));
            });

        Note = Failure is null ? $"Uploaded {chosen.Count} file{(chosen.Count == 1 ? "" : "s")}" : null;
        await Refresh();
    }

    /// <summary>
    /// Copies the selected file into the Downloads folder.
    ///
    /// The same answer the browser gives, for the same reason: somewhere rather
    /// than nowhere, with the note saying where it went.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public async Task Download()
    {
        if (Selected is not { IsDirectory: false } node || Workspace is not { } folder)
            return;

        var destination = System.IO.Path.Combine(DownloadsFolder(), node.Name);
        Note = null;
        await Do($"Downloading {node.Name}", () => folder.Download(node.Relative, destination));
        if (Failure is null)
            Note = $"Saved to {destination}";
    }

    /// <summary>Deletes the selection, after asking. A directory says what goes with it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public async Task Delete()
    {
        if (Selected is not { } node || Workspace is not { } folder)
            return;

        var detail = node.IsDirectory
            ? "Everything inside it goes too, and none of it can be undone."
            : "It cannot be undone.";
        if (!await _dialogs.Confirm($"Delete {node.Name}?", detail, "Delete"))
            return;

        await Do($"Deleting {node.Name}", () => folder.Delete(node.Entry));
        if (Failure is null && Open.FirstOrDefault(editor => editor.Relative == node.Relative) is { } editor)
        {
            // The file is gone; the tab holding it is not a document any more.
            Open.Remove(editor);
            if (Current == editor)
                Current = Open.LastOrDefault();
        }
        await Refresh();
    }

    /// <summary>
    /// Naming something, in the tree rather than in a dialog.
    ///
    /// A modal window to type six characters into is the Swift app's habit, not
    /// this one's: the name belongs where the thing will appear.
    /// </summary>
    [ObservableProperty]
    public partial bool IsNaming { get; private set; }

    [ObservableProperty]
    public partial string NameDraft { get; set; } = "";

    /// <summary>What the inline field is for, which is also what its label says.</summary>
    [ObservableProperty]
    public partial string NamingLabel { get; private set; } = "";

    private enum Naming
    {
        File,
        Folder,
        Rename,
    }

    private Naming _naming;

    private WorkspaceNode? _renaming;

    [RelayCommand]
    public void NewFile() => Name(Naming.File, "New file in " + NamingInto(), "");

    [RelayCommand]
    public void NewFolder() => Name(Naming.Folder, "New folder in " + NamingInto(), "");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void Rename()
    {
        if (Selected is not { } node)
            return;
        _renaming = node;
        Name(Naming.Rename, $"Rename {node.Name} to", node.Name);
    }

    private void Name(Naming what, string label, string value)
    {
        _naming = what;
        NamingLabel = label;
        NameDraft = value;
        IsNaming = true;
    }

    [RelayCommand]
    public void CancelName()
    {
        IsNaming = false;
        NameDraft = "";
        _renaming = null;
    }

    /// <summary>Carries out whatever the inline field was for.</summary>
    [RelayCommand]
    public async Task ConfirmName()
    {
        // Nothing to do until a folder has been chosen, which on this machine
        // is a deliberate act rather than a default.
        if (Workspace is not { } folder)
            return;

        var name = NameDraft.Trim();
        if (name.Length == 0)
        {
            CancelName();
            return;
        }

        var naming = _naming;
        var renaming = _renaming;
        CancelName();

        if (naming == Naming.Rename)
        {
            if (renaming is null)
                return;
            var parent = PosixPath.Parent(renaming.Relative);
            var moved = parent is null or "" ? name : PosixPath.Join(parent, name);
            await Do($"Renaming {renaming.Name}", () => folder.Rename(renaming.Relative, moved));
            await Refresh();
            return;
        }

        var into = PosixPath.Join(Directory(), name);
        if (naming == Naming.Folder)
            await Do($"Creating {name}", () => folder.CreateDirectory(into));
        else
            await Do($"Creating {name}", () => folder.CreateFile(into));

        await Refresh();
        if (Failure is null && naming == Naming.File)
            await OpenFile(into);
    }

    /// <summary>
    /// Where a new file goes: into the selected directory, or beside the
    /// selected file, or the root when nothing is selected. What every file
    /// tree does, and the only answer nobody has to think about.
    /// </summary>
    private string Directory() => Selected switch
    {
        { IsDirectory: true } node => node.Relative,
        { } node => PosixPath.Parent(node.Relative) ?? "",
        null => "",
    };

    /// <summary>Where a new file or folder would go, as the naming bar says it.</summary>
    private string NamingInto()
    {
        var into = Directory();
        return into.Length == 0 ? Root : into;
    }

    /// <summary>
    /// The assistant wrote a file in this folder.
    ///
    /// The tree and the editors have to catch up, or the pane goes on showing
    /// what the file said before — which is worse than showing nothing, because
    /// it looks current. An editor with unsaved work in it is left alone and
    /// told to say so: throwing away what somebody typed because a model wrote
    /// to the same file would be the pane picking a winner.
    /// </summary>
    public async Task Changed(FileChange change)
    {
        // Nothing to do until a folder has been chosen, which on this machine
        // is a deliberate act rather than a default.
        if (Workspace is not { } folder)
            return;

        var clash = false;
        if (Open.FirstOrDefault(editor => editor.Relative == change.Relative) is { } editor)
        {
            clash = editor.IsDirty;
            if (!clash)
            {
                FileText? read = null;
                await Do($"Reading {change.Relative}", () => read = folder.Read(change.Relative));
                if (read is not null)
                    editor.Reloaded(read);
            }
        }

        // The tree last, and what to say after it: every call through Do clears
        // the banner, so a message set before one is a message nobody sees.
        await Refresh();

        Note = $"The assistant wrote {change.Relative}";
        if (clash)
        {
            Failure = $"{change.Relative} was changed by the assistant while you were editing it. "
                + "What is on screen is still yours; saving replaces what it wrote.";
        }
    }

    private static string DownloadsFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = System.IO.Path.Combine(home, "Downloads");
        return System.IO.Directory.Exists(downloads) ? downloads : home;
    }

    /// <summary>
    /// What to tell the user. The two SFTP cases worth naming are the two that
    /// are not this app's fault, and the bounds one is this app's whole point.
    /// </summary>
    private static string Explain(Exception error) => error switch
    {
        WorkspaceBoundsException bounds => bounds.Message,
        Renci.SshNet.Common.SftpPermissionDeniedException => "permission denied",
        Renci.SshNet.Common.SftpPathNotFoundException => "no such file or directory",
        _ => error.Message,
    };
}
