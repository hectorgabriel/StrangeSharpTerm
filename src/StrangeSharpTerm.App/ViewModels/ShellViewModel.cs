using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Assistant;
using StrangeSharpTerm.App.Terminal;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Mcp;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>One tab as the tab strip needs it: a name, and whether it has focus.</summary>
public sealed record TabItem(NodeId Id, string Title, bool IsActive);

/// <summary>
/// One pane as the window needs it: what to draw, and whether the keyboard is
/// in it. A split tab is a list of these along one axis.
/// </summary>
/// <param name="Title">
/// What to call it when the layout has to say so itself. Tiled there is no tab
/// strip naming the sessions, and a grid of four terminals that are only told
/// apart by reading their prompts is a grid nobody can navigate.
/// </param>
public sealed record PaneSlot(NodeId Id, Control View, bool IsActive, string Title = "");

/// <summary>
/// What the panel down the left-hand side is showing.
///
/// The mirror of <see cref="DockView"/> on the other side of the window: one
/// panel, two things it can be, and a switch at the top saying which.
/// </summary>
public enum SidebarView
{
    /// <summary>The inventory: folders and the servers in them.</summary>
    Hosts,

    /// <summary>This machine's own files, rooted at a folder somebody chose.</summary>
    Files,
}

/// <summary>What the dock down the right-hand side is showing.</summary>
public enum DockView
{
    /// <summary>
    /// A conversation about one host: whichever session has the keyboard.
    ///
    /// It follows the focus rather than being pinned, because the dock is beside
    /// every session at once and an assistant that stayed on the host you opened
    /// it from would, in a tiled window, be talking about a terminal you are no
    /// longer looking at.
    /// </summary>
    Assistant,

    /// <summary>One instruction across several hosts, which belongs to none of them.</summary>
    Orchestrator,
}

/// <summary>
/// What the window binds to: the inventory on the left, the workspace on the
/// right, and the few actions that join them.
///
/// The Swift app did this joining inside the god object, which is how selecting a
/// host, opening a tab and closing a session all ended up in one place. Here the
/// two halves stay ignorant of each other and this arranges the introduction.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly TerminalRegistry _terminals = new();
    private readonly IHostSessions _sessions;
    private readonly Dictionary<NodeId, DashboardViewModel> _dashboards = [];
    // One folder per host, not one per pane: "the folder open on web-01" has to
    // be a question with one answer, because the assistant asks it too.
    private readonly Dictionary<NodeId, HostWorkspaceViewModel> _workspaces = [];
    private readonly Dictionary<NodeId, NodeId> _workspacePanes = [];
    private readonly Dictionary<NodeId, AssistantViewModel> _conversations = [];
    private readonly Func<TerminalSession, TerminalPalette, Control> _view;
    private readonly IDialogService _dialogs;
    private readonly Lazy<ISecretStore> _secrets;
    private readonly Lazy<ISecretStore> _assistKeys;
    private readonly Lazy<ISecretStore> _toolTokens;
    private readonly AppTheme _theme;
    private readonly Func<AssistSettings, IAssistBackend?> _backends;
    private readonly string? _preferencesPath;
    private McpHub? _tools;

    /// <param name="sessions">
    /// Everything a host can be asked for, over one connection to it. Replaced
    /// in tests by something that needs no server.
    /// </param>
    /// <param name="view">How a session becomes something on screen. Likewise.</param>
    public ShellViewModel(
        InventoryViewModel inventory,
        IHostSessions? sessions = null,
        Func<TerminalSession, TerminalPalette, Control>? view = null,
        IDialogService? dialogs = null,
        AppTheme? theme = null,
        ISecretStore? secrets = null,
        AssistSettings? assist = null,
        Func<AssistSettings, IAssistBackend?>? backends = null,
        string? preferencesPath = null,
        McpHub? tools = null,
        ISecretStore? assistKeys = null,
        ISecretStore? toolTokens = null,
        Func<OrchestratorViewModel, Control>? orchestratorView = null,
        Func<AssistantViewModel, Control>? assistantView = null,
        Func<HostWorkspaceViewModel, Control>? workspaceView = null,
        IRemoteFiles? localFiles = null,
        Func<HostWorkspaceViewModel, Control>? editorView = null)
    {
        Inventory = inventory;
        _dialogs = dialogs ?? new ScriptedDialogService();
        // Opened when the library is, not when the window is: reaching for the
        // platform keychain costs a round trip and can refuse, and neither
        // belongs in the constructor of the thing that draws the sidebar.
        _secrets = Store(secrets, PlatformSecretStore.DefaultService);
        // Three kinds of secret, three services, and each read from the one it
        // was written to. They are separate because they are revoked, rotated
        // and lost independently: an API key is not a server passphrase, and a
        // tool server's token is neither.
        _assistKeys = Store(assistKeys, AssistKeys.Service);
        _toolTokens = Store(toolTokens, McpTokens.Service);
        _theme = theme ?? new AppTheme();
        _preferencesPath = preferencesPath;
        // The assistant's settings are the app's, not a pane's: the preview a
        // pane shows is of a choice made once, somewhere a person can find it.
        AssistantSettings = assist ?? (preferencesPath is { } path ? AssistPreferences.Load(path) : new AssistSettings());
        // Substituted in tests, and the only place a provider is built. The
        // store is the assistant's own -- reading it from the connection store
        // is what made every saved key invisible.
        _backends = backends ?? (settings => AssistBackends.For(settings, _assistKeys.Value));
        _tools = tools;
        Workspace = new WorkspaceViewModel(_terminals);
        _sessions = sessions ?? new HostSessions(() => Inventory.Tree);
        _view = view ?? ((session, palette) => new TerminalPaneView(session, palette, _terminals));
        // Substituted for the same reason as _view: building the real control
        // means running its XAML, and a unit test with no application around it
        // races Avalonia's weak-event bookkeeping rather than testing anything.
        _orchestratorView = orchestratorView ?? (model => new OrchestratorView(model));
        // Substituted for the same reason, and newly so: the assistant is in the
        // dock now, which a test of the layout builds without an application
        // around it to run the control's XAML in.
        _assistantView = assistantView ?? (model => new AssistantView(model));
        _workspaceView = workspaceView ?? (model => new WorkspaceView(model));
        _editorView = editorView ?? (model => new WorkspaceEditorView(model));
        // Which folder each host was last opened at. Read once, here, because
        // opening a pane should not be a file read.
        _roots = new Dictionary<NodeId, string>(WorkspacePreferences.Load(preferencesPath));

        // This machine's own files. Built here and not read from until somebody
        // opens the Files side of the sidebar: constructing it costs nothing,
        // and listing a directory is what costs.
        LocalFiles = new HostWorkspaceViewModel(
            localFiles ?? new Transport.LocalFiles(),
            Environment.MachineName,
            WorkspacePreferences.LocalRoot(preferencesPath),
            _dialogs,
            remember: root => WorkspacePreferences.SaveLocal(_preferencesPath, root),
            isLocal: true)
        {
            // A file picked here goes to whichever host has a folder open,
            // which is the one thing the two workspaces do together.
            Sender = SendToHost,
        };

        LocalFiles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HostWorkspaceViewModel.Current))
                OpenLocalEditor();
        };
        LocalFiles.RootChanged += (_, _) =>
        {
            // Every conversation, not one: this folder belongs to no host, so
            // all of them gain and lose its tools together -- the fan-out
            // included.
            foreach (var conversation in _conversations.Values)
                conversation.WorkspaceChanged();
            (_orchestrator?.DataContext as OrchestratorViewModel)?.LocalFilesChanged();
        };
        LocalFiles.Saveability += (_, _) =>
        {
            OnPropertyChanged(nameof(CanSaveFile));
            SaveFileCommand.NotifyCanExecuteChanged();
        };

        // Focusing a pane moves the sidebar with it, and deleting a host closes
        // whatever it had open. Neither half knows about the other.
        Workspace.HostFocused += (_, host) => Inventory.Selection = host;
        Inventory.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InventoryViewModel.Selection) or nameof(InventoryViewModel.Tree))
            {
                RefreshDetail();
                OnPropertyChanged(nameof(ShowsDetail));
                OnPropertyChanged(nameof(ShowsPanes));
                OnPropertyChanged(nameof(ShowsEmptyState));
                RefreshCommands();
            }
        };
        Inventory.ConnectionRemoving += (_, host) => CloseEverythingFor(host);
        Workspace.PropertyChanged += (_, _) => RefreshTabs();

        // Changing the theme repaints the open terminals where they stand. The
        // window's own colours are resources and need nobody to tell them.
        _theme.Changed += (_, _) => Recolour();
    }

    public InventoryViewModel Inventory { get; }

    public WorkspaceViewModel Workspace { get; }

    /// <summary>
    /// The connected tool servers, or null when none are configured.
    ///
    /// Built once and shared by every pane: a server launched per pane would be
    /// a process per pane, and an HTTP one would be a sign-in per pane.
    /// </summary>
    public McpHub? Tools => _tools;

    /// <summary>
    /// Connects the configured tool servers, in the background.
    ///
    /// Never throws and never blocks the window: a server that will not start is
    /// a row in Settings that says so, and the app is entirely usable without
    /// any of them.
    /// </summary>
    public async Task ConnectTools()
    {
        if (_preferencesPath is not { } path)
            return;

        var settings = McpPreferences.Load(path);
        if (settings.Usable.Count == 0)
            return;

        _tools ??= new McpHub(settings, saved => McpPreferences.Save(path, saved), _toolTokens.Value);
        try
        {
            await _tools.Connect();
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"connecting tool servers failed: {e.Message}");
        }
    }

    /// <summary>
    /// What a pane is given. Null when there are none, or when the switch for
    /// this kind of pane is off.
    /// </summary>
    private IExternalTools? ToolsFor(bool inARun)
    {
        if (_tools is not { } hub || hub.Offered.Count == 0)
            return null;
        return inARun ? hub.Settings.OfferInRuns ? hub : null : hub.Settings.OfferInPanes ? hub : null;
    }

    /// <summary>
    /// How the assistant is configured, for every pane in this window.
    ///
    /// One setting rather than one per pane: which provider is answering decides
    /// where this window's terminal output is being sent, and that is not a
    /// per-conversation choice.
    /// </summary>
    public AssistSettings AssistantSettings { get; private set; }

    public ObservableCollection<TabItem> Tabs { get; } = [];

    /// <summary>
    /// The focused tab's panes, in the order they were opened.
    ///
    /// A list rather than one view: a tab that has been split shows all of them
    /// at once, and only one of them has the keyboard.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<PaneSlot> Panes { get; private set; } = [];

    /// <summary>
    /// Every open pane, across every tab.
    ///
    /// The layout needs this to tell a pane that has closed from one that is
    /// merely in another tab. Without it, switching tabs took the panes it was
    /// leaving out of the visual tree, which is fatal to a terminal: the control
    /// tears its connection down and relaunches a process of its own, so the tab
    /// came back showing "Process exited with code: 0" and a local shell.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<NodeId> OpenPanes { get; private set; } = [];

    /// <summary>Which way the focused tab's panes are laid out.</summary>
    public SplitAxis Axis => Workspace.ActiveTab?.Axis ?? SplitAxis.Horizontal;

    /// <summary>
    /// Whether the middle of the window is showing every session at once.
    ///
    /// The workspace owns the answer, because it also decides where a broadcast
    /// goes; this is the window's way of asking.
    /// </summary>
    public bool IsTiled => Workspace.IsTiled;

    /// <summary>
    /// Every session in a grid, or one tab at a time.
    ///
    /// The tabs are not thrown away by tiling, and nothing is opened or closed
    /// by it: the same panes are laid out differently, which is why this only
    /// has to ask for them again.
    /// </summary>
    [RelayCommand]
    public void ToggleTiles()
    {
        Workspace.IsTiled = !Workspace.IsTiled;
        OnPropertyChanged(nameof(IsTiled));
        Show();
    }

    /// <summary>
    /// Whether the inventory is showing down the left.
    ///
    /// Collapsible because three panes do not fit a small window otherwise: at
    /// the 720 the window will shrink to, a fixed sidebar and a docked assistant
    /// leave less room for the sessions than one terminal needs.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; } = true;

    [RelayCommand]
    public void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    /// <summary>
    /// What the left panel is showing: the servers, or this machine's files.
    ///
    /// One panel with two things in it rather than two panels, because they are
    /// alternatives in practice -- you are picking a server to work on or a file
    /// to work on -- and 260 pixels split in half is a tree nobody can read.
    /// </summary>
    [ObservableProperty]
    public partial SidebarView SidebarShows { get; private set; } = SidebarView.Hosts;

    public bool SidebarShowsHosts => SidebarShows == SidebarView.Hosts;

    public bool SidebarShowsFiles => SidebarShows == SidebarView.Files;

    partial void OnSidebarShowsChanged(SidebarView value)
    {
        OnPropertyChanged(nameof(SidebarShowsHosts));
        OnPropertyChanged(nameof(SidebarShowsFiles));
    }

    [RelayCommand]
    public void ShowHosts()
    {
        SidebarShows = SidebarView.Hosts;
        IsSidebarOpen = true;
    }

    /// <summary>
    /// Shows this machine's files, opening the folder the first time anybody
    /// asks -- which is when the cost of reading a directory is worth paying.
    /// </summary>
    [RelayCommand]
    public async Task ShowFiles()
    {
        SidebarShows = SidebarView.Files;
        IsSidebarOpen = true;
        if (LocalFiles.Tree.Count == 0)
            await LocalFiles.RefreshCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// This machine's files: the same workspace a host gets, over the same
    /// seam, rooted here.
    ///
    /// Built once and kept, because the sidebar shows it whether or not
    /// anything is connected -- it belongs to no host, and nothing can
    /// disconnect it.
    /// </summary>
    public HostWorkspaceViewModel LocalFiles { get; }

    /// <summary>Which of the two conversations the right-hand dock is showing.</summary>
    [ObservableProperty]
    public partial DockView DockShows { get; private set; } = DockView.Assistant;

    public bool DockShowsAssistant => DockShows == DockView.Assistant;

    public bool DockShowsOrchestrator => DockShows == DockView.Orchestrator;

    partial void OnDockShowsChanged(DockView value)
    {
        OnPropertyChanged(nameof(DockShowsAssistant));
        OnPropertyChanged(nameof(DockShowsOrchestrator));
    }

    /// <summary>Whether the dock has a column of its own at all.</summary>
    [ObservableProperty]
    public partial bool IsDockOpen { get; private set; }

    /// <summary>
    /// What the dock is drawing: one host's assistant, or the orchestrator.
    ///
    /// A control rather than a view model, for the same reason a pane is: which
    /// of the two views this is depends on the kind of conversation, and the
    /// window should not have to know.
    /// </summary>
    [ObservableProperty]
    public partial Control? Dock { get; private set; }

    /// <summary>
    /// The name above the dock: the host whose assistant it is showing.
    ///
    /// Empty for the orchestrator, which is deliberately about several.
    /// </summary>
    [ObservableProperty]
    public partial string DockTitle { get; private set; } = "";

    [RelayCommand]
    public void CloseDock() => IsDockOpen = false;

    /// <summary>
    /// Whether the right-hand side is showing the selected host rather than a
    /// terminal: either nothing is open, or the selection is a different host
    /// from the one the focused pane belongs to.
    ///
    /// The detail shares its row with the panes and is drawn after them, so
    /// showing it covers whatever is open. A pane that belongs to no single host
    /// -- the orchestrator, which is deliberately about several -- has no host to
    /// differ from the selection, and reading its null as "a different host" is
    /// what drew the detail panel on top of it.
    /// </summary>
    /// <remarks>
    /// Tiled, the rule is different, because the panes are no longer one host's
    /// business: covering every session on screen because the sidebar selection
    /// moved would hide seven servers to describe one. So a tiled window shows
    /// the detail only for a host with nothing open — which is the state the
    /// detail is for, since it is where a session is started from.
    /// </remarks>
    public bool ShowsDetail =>
        Detail is not null
        && (Panes.Count == 0
            || (Workspace.IsTiled
                ? Workspace.Panes.All(pane => pane.ConnectionId != Inventory.Selection)
                : Workspace.ActivePane is { ConnectionId: { } host } && host != Inventory.Selection));

    /// <summary>
    /// Whether the panes are on screen at all.
    ///
    /// Exactly one of the three things sharing that row shows. Leaving the panes
    /// drawn underneath and trusting the detail's own background to hide them is
    /// what the first attempt did, and the two are inset by different margins --
    /// so a ring of live terminal showed around the detail panel, its banner
    /// along the top and its text down the side. A pane that is not being shown
    /// should not be drawn.
    ///
    /// Hidden, not removed: the panes stay in the visual tree, because a
    /// terminal control taken out of it loses the connection it was given. That
    /// is the same rule the tab switch is built on -- see
    /// <see cref="Views.PaneSplitView"/>.
    /// </summary>
    public bool ShowsPanes => Panes.Count > 0 && !ShowsDetail;

    /// <summary>
    /// Nothing selected and nothing open. Not simply "no detail": a terminal is
    /// showing whenever a pane is open, and an empty-state line drawn over it
    /// reads as part of the shell's output.
    /// </summary>
    public bool ShowsEmptyState => Panes.Count == 0 && !ShowsDetail;

    /// <summary>What went wrong with the last connection attempt, for the window to show.</summary>
    [ObservableProperty]
    public partial string? Failure { get; private set; }

    /// <summary>
    /// The selected host, resolved. Null when nothing is selected, or when the
    /// selection names a host that has since been deleted.
    /// </summary>
    [ObservableProperty]
    public partial HostDetailViewModel? Detail { get; private set; }

    /// <summary>
    /// Rebuilds the detail pane for whatever is selected.
    ///
    /// The dashboard inside it is kept per host rather than rebuilt with the
    /// pane: it holds what the server last said and the little history the
    /// sparkline draws, and editing a host in the middle of watching it should
    /// not throw that away.
    /// </summary>
    private void RefreshDetail()
    {
        if (Inventory.Selection is not { } id || !Inventory.Tree.Connections.TryGetValue(id, out var connection))
        {
            Detail = null;
            return;
        }

        if (!_dashboards.TryGetValue(id, out var dashboard))
            _dashboards[id] = dashboard = new DashboardViewModel(_sessions.Health(connection));

        var resolved = Inventory.Tree.Resolve(id);
        Detail = new HostDetailViewModel(
            resolved,
            dashboard,
            resolved.Settings.CredentialId is { } credential
                ? Inventory.Tree.Credentials.GetValueOrDefault(credential)
                : null);
    }

    /// <summary>
    /// Adds a host, in whichever folder the user is looking at.
    ///
    /// A new host lands where the eye already is: inside the selected folder, or
    /// beside the selected host, or at the top level when nothing is selected.
    /// Landing it at the top level regardless would be a small thing to fix by
    /// hand every single time.
    /// </summary>
    [RelayCommand]
    public async Task NewHost()
    {
        var parent = FolderInFocus();
        var draft = HostDraft.New(Inventory.Tree, parent, Inventory.NextSortIndex(parent));
        if (await _dialogs.Edit(draft))
            Inventory.Upsert(draft.Applied());
    }

    /// <inheritdoc cref="NewHost"/>
    [RelayCommand]
    public async Task NewFolder()
    {
        var parent = FolderInFocus();
        var draft = FolderDraft.New(Inventory.Tree, parent, Inventory.NextSortIndex(parent));
        if (await _dialogs.Edit(draft))
        {
            var folder = draft.Applied();
            Inventory.Upsert(folder);
            Inventory.Selection = null;
        }
    }

    /// <summary>
    /// Whether a host is chosen. Three commands need one, and a menu that offers
    /// "Edit Host" with nothing selected is a menu that lies.
    /// </summary>
    public bool HasSelectedHost => Detail is not null;

    /// <summary>Whether there is a pane to close, split or act on.</summary>
    public bool HasOpenPane => Workspace.ActivePaneId is not null;

    /// <summary>
    /// The credential library: a key or a password described once, pointed at
    /// from any number of hosts.
    /// </summary>
    [RelayCommand]
    public async Task ManageCredentials() =>
        await _dialogs.Manage(new CredentialsViewModel(Inventory, _secrets.Value, _dialogs));

    /// <summary>
    /// The settings sheet: the theme, and the way in to both libraries.
    ///
    /// The libraries are opened <em>through</em> it rather than listed in it, so
    /// there is one implementation of each and the sheet is a signpost.
    /// </summary>
    [RelayCommand]
    public async Task OpenSettings()
    {
        var settings = new SettingsViewModel(
            _theme,
            () => ManageCredentialsCommand.ExecuteAsync(null),
            () => ManageSnippetsCommand.ExecuteAsync(null),
            AssistantSettings,
            // API keys go to a store of their own, never the one connection
            // credentials use: an API key is not a server secret.
            _assistKeys.Value,
            // Applied as it is changed rather than on Done, as the theme is:
            // there is nothing here to confirm, and a pane opened next reads it.
            assist =>
            {
                AssistantSettings = assist;
                if (_preferencesPath is { } path)
                    AssistPreferences.Save(path, assist);
            },
            // Built on demand, so a window with no servers configured never
            // constructs a hub -- and so the sheet is the place a first one can
            // be added.
            _tools ??= _preferencesPath is { } where
                ? new McpHub(McpPreferences.Load(where), saved => McpPreferences.Save(where, saved), _toolTokens.Value)
                : null,
            _dialogs,
            _toolTokens.Value);

        await _dialogs.Manage(settings);
    }

    /// <summary>
    /// A store, opened when it is first used rather than when the window is:
    /// reaching for the platform keychain costs a round trip and can refuse, and
    /// neither belongs in the constructor of the thing that draws the sidebar.
    /// </summary>
    private static Lazy<ISecretStore> Store(ISecretStore? given, string service) =>
        given is { } substituted
            ? new Lazy<ISecretStore>(substituted)
            : new Lazy<ISecretStore>(() => new PlatformSecretStore(service));

    /// <summary>The snippet library: commands worth keeping, and where each is offered.</summary>
    [RelayCommand]
    public async Task ManageSnippets()
    {
        var snippets = new SnippetsViewModel(Inventory, _dialogs);
        snippets.Refresh();
        await _dialogs.Manage(snippets);
        // The palette offers snippets, so anything added or renamed in there has
        // to be visible the next time it opens.
        RefreshCommands();
    }

    /// <summary>
    /// A snippet needs a shell to type into, and a browser or a dashboard is not
    /// one. Checked rather than assumed: the palette offers only what can run.
    /// </summary>
    public bool CanRunSnippet(Snippet? snippet) => snippet is not null && ActiveTerminal() is not null;

    /// <summary>
    /// Types a saved command into the focused shell and runs it.
    ///
    /// Parameterised snippets ask first, and the dialog's preview is the last
    /// thing shown before the line is sent — this runs on a live server, and
    /// what a placeholder expanded to is the thing worth seeing.
    ///
    /// Sent rather than written, so broadcast fans it out exactly as it fans out
    /// typing: a snippet in a broadcast group reaches every pane in the group.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunSnippet))]
    public async Task RunSnippet(Snippet? snippet)
    {
        if (snippet is null || ActiveTerminal() is not { } session)
            return;

        var line = snippet.Command;
        if (snippet.IsParameterised)
        {
            var filling = new SnippetRunViewModel(
                snippet,
                HostOfActivePane() ?? "this shell",
                IsBroadcasting ? Workspace.ActiveTab?.PaneIds.Count ?? 1 : 1);

            if (!await _dialogs.Fill(filling))
                return;
            line = filling.Preview;
        }

        // Carriage return, because that is what a terminal sends when a person
        // presses Enter. A Unix pty translates a line feed; Windows does not, so
        // a line feed there is typed and never run.
        session.Send(line + "\r");
    }

    /// <summary>
    /// The host the focused pane is about, by the name the inventory gives it.
    ///
    /// Not the pane's title: the far end sets that, and a shell prompt —
    /// "ops@ip-10-0-1-7:~" — is not how anyone identifies the server they are
    /// about to run something on.
    /// </summary>
    private string? HostOfActivePane() =>
        Workspace.ActivePane?.ConnectionId is { } host
            ? Inventory.Tree.Connections.GetValueOrDefault(host)?.Name
            : null;

    /// <summary>The focused pane's shell, when the focused pane is one.</summary>
    private TerminalSession? ActiveTerminal() =>
        Workspace.ActivePane is { IsTerminal: true, Id: var pane } ? _terminals.Session(pane) : null;

    /// <summary>Edits the selected host, for the button in the detail pane.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedHost))]
    public async Task EditSelected()
    {
        if (Detail is { } detail)
            await Edit(detail.Connection.Id);
    }

    /// <summary>Edits whatever a row is: a host or a folder, from its own menu.</summary>
    [RelayCommand]
    public async Task EditRow(SidebarRow? row)
    {
        if (row is not null)
            await Edit(row.Id);
    }

    /// <summary>Deletes whatever a row is, after asking. The question names what goes with it.</summary>
    [RelayCommand]
    public async Task DeleteRow(SidebarRow? row)
    {
        if (row is null)
            return;

        Inventory.ConfirmDelete(row.IsFolder
            ? new PendingDeletion.Folder(row.Id)
            : new PendingDeletion.Connection(row.Id));
        await ConfirmPendingDeletion();
    }

    private async Task Edit(NodeId id)
    {
        if (Inventory.Tree.Connections.GetValueOrDefault(id) is { } connection)
        {
            var draft = HostDraft.For(Inventory.Tree, connection);
            if (await _dialogs.Edit(draft))
                Inventory.Upsert(draft.Applied());
            return;
        }

        if (Inventory.Tree.Folders.GetValueOrDefault(id) is { } folder)
        {
            var draft = FolderDraft.For(Inventory.Tree, folder);
            if (await _dialogs.Edit(draft))
                Inventory.Upsert(draft.Applied());
        }
    }

    /// <summary>
    /// Where a new host or folder belongs: the selected folder, or the folder the
    /// selected host is in, or nowhere in particular.
    /// </summary>
    private NodeId? FolderInFocus()
    {
        if (Inventory.Selection is not { } selected)
            return null;
        if (Inventory.Tree.Folders.ContainsKey(selected))
            return selected;
        return Inventory.Tree.Connections.GetValueOrDefault(selected)?.ParentId;
    }

    /// <summary>Opens a shell on the selected host, for the button in the detail pane.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedHost))]
    public async Task ConnectSelected()
    {
        if (Detail is { } detail)
            await OpenTerminal(detail.Connection);
    }

    /// <summary>
    /// Deletes the selected host, after asking. The confirmation names what goes
    /// with it rather than merely asking twice.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedHost))]
    public async Task DeleteSelected()
    {
        if (Detail is not { } detail)
            return;

        Inventory.ConfirmDelete(new PendingDeletion.Connection(detail.Connection.Id));
        await ConfirmPendingDeletion();
    }

    private async Task ConfirmPendingDeletion()
    {
        if (Inventory.PendingDeletionMessage is not { } message)
        {
            // Nothing to describe means nothing to delete: the row named something
            // that is no longer there.
            Inventory.Pending = null;
            return;
        }

        if (await _dialogs.Confirm(message.Title, message.Detail, "Delete"))
            Inventory.PerformPendingDeletion();
        else
            Inventory.Pending = null;
    }

    /// <summary>
    /// A row was activated: a folder opens or shuts, a host is selected.
    ///
    /// Selecting does not connect. The detail pane says what a connection would
    /// use and offers a button for it, so opening a shell stays something asked
    /// for rather than something a stray click does to a production server.
    /// </summary>
    [RelayCommand]
    public void Activate(SidebarRow? row)
    {
        if (row is null)
            return;
        if (row.IsFolder)
            Inventory.Toggle(row.Id);
        else
            Inventory.Selection = row.Id;
    }

    /// <summary>
    /// Opens a second shell on the focused pane's host, beside it.
    ///
    /// A split is another session on the same server, which is what splitting is
    /// for: a log tailing on one side, a command on the other. It is not a second
    /// view of the same shell — ssh has no such thing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasOpenPane))]
    public async Task SplitRight() => await Split(SplitAxis.Horizontal);

    /// <inheritdoc cref="SplitRight"/>
    [RelayCommand(CanExecute = nameof(HasOpenPane))]
    public async Task SplitDown() => await Split(SplitAxis.Vertical);

    private async Task Split(SplitAxis axis)
    {
        if (Workspace.ActivePane?.ConnectionId is not { } host)
            return;
        if (Inventory.Tree.Connections.GetValueOrDefault(host) is { } connection)
            await OpenTerminal(connection, axis);
    }

    /// <summary>Moves the keyboard to a pane, and the sidebar with it.</summary>
    [RelayCommand]
    public void FocusPane(NodeId id)
    {
        Workspace.FocusPane(id);
        Show();
    }

    /// <summary>Closes the focused pane, and the tab with it when it was the last.</summary>
    [RelayCommand(CanExecute = nameof(HasOpenPane))]
    public void ClosePane()
    {
        if (Workspace.ActivePaneId is not { } pane)
            return;
        Workspace.ClosePane(pane);
        Show();
    }

    /// <summary>Whether typing goes to every terminal in the focused tab.</summary>
    public bool IsBroadcasting
    {
        get => Workspace.IsBroadcasting;
        set
        {
            Workspace.IsBroadcasting = value;
            OnPropertyChanged();
        }
    }

    /// <summary>A group of one is not a broadcast, so the toggle waits for a split.</summary>
    public bool CanBroadcast => Workspace.CanBroadcast;

    /// <summary>For the menu and the palette, which need a command rather than a property.</summary>
    [RelayCommand(CanExecute = nameof(CanBroadcast))]
    public void ToggleBroadcast() => IsBroadcasting = !IsBroadcasting;

    /// <summary>Everything the app can be asked to do, and the way in by name.</summary>
    public PaletteViewModel Palette { get; private set; } = null!;

    public IReadOnlyList<AppCommand> Commands { get; private set; } = [];

    /// <summary>
    /// Builds the command list, once the view model is whole.
    ///
    /// Late because the catalogue holds this object's own commands: it cannot be
    /// built in the constructor without handing out a half-built shell.
    /// </summary>
    public void Describe(KeyModifiers commandModifier)
    {
        Commands = CommandCatalogue.For(this, commandModifier);
        // The palette is given a function rather than a list because half of what
        // it offers is not fixed: the snippets depend on which host is selected,
        // and the library can be edited while the window is open. The menu and the
        // key bindings keep the fixed list.
        Palette = new PaletteViewModel(() => [.. Commands, .. SnippetCommands()]);
        OnPropertyChanged(nameof(Commands));
        OnPropertyChanged(nameof(Palette));
    }

    /// <summary>
    /// The snippets offered right now, as palette entries.
    ///
    /// Only the ones this host is in scope for: a snippet scoped to a folder is
    /// offered beneath it and nowhere else, which is the whole point of the
    /// scope. They are built fresh each time the palette opens rather than kept,
    /// because both the selection and the library change under it.
    /// </summary>
    public IReadOnlyList<AppCommand> SnippetCommands() =>
    [
        .. Inventory.VisibleSnippets.Select(snippet => new AppCommand(
            $"snippet.{snippet.Id}",
            snippet.Name,
            CommandGroup.Session,
            RunSnippetCommand,
            Parameter: snippet)),
    ];

    [RelayCommand]
    public void OpenPalette() => Palette.Open();

    /// <summary>⌘5 with two tabs open does nothing rather than something odd.</summary>
    public bool CanFocusTabAt(int index) => index >= 0 && index < Tabs.Count;

    [RelayCommand(CanExecute = nameof(CanFocusTabAt))]
    public void FocusTabAt(int index)
    {
        Workspace.FocusTabAt(index);
        Show();
    }

    [RelayCommand]
    public void FocusTab(TabItem? tab)
    {
        if (tab is not null)
            Workspace.FocusTab(tab.Id);
        Show();
    }

    [RelayCommand]
    public void CloseTab(TabItem? tab)
    {
        if (tab is not null)
            Workspace.CloseTab(tab.Id);
        Show();
    }

    /// <summary>Opens a file browser on the selected host, beside whatever is open.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedHost))]
    public async Task BrowseFiles()
    {
        if (Detail is not { } detail)
            return;

        Failure = null;
        var connection = detail.Connection;
        var pane = new Pane { Title = $"{connection.Name} files", Kind = new PaneKind.Files(), ConnectionId = connection.Id };

        try
        {
            // Off the UI thread for the same reason a connection is: SFTP costs
            // its own authentication (see SshNetSession.OpenSftp), and a window
            // that freezes while a host times out is what that would cost.
            var files = await Task.Run(() => _sessions.Files(connection));
            var browser = new FileBrowserViewModel(files, connection.Name, _dialogs);

            Workspace.Open(pane, Panes.Count > 0 ? Workspace.ActiveTab?.Axis ?? SplitAxis.Horizontal : null);
            _views[pane.Id] = _browser(browser);
            Show();
            await browser.Refresh();
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"browsing {connection.Name} failed: {e}");
            Failure = SshFailure.Classify(e).Summary;
        }
    }

    /// <summary>How a browser becomes something on screen. Replaced in tests.</summary>
    private readonly Func<FileBrowserViewModel, Control> _browser = model => new FileBrowserView(model);

    /// <summary>
    /// Opens a folder on the selected host as a workspace, beside whatever is
    /// open.
    ///
    /// One per host: asking again brings the one that is open forward rather
    /// than opening a second. Two panes rooted at two folders would make "the
    /// folder open on this host" a question with two answers, and that question
    /// is what the assistant's file tools are judged against.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedHost))]
    public async Task OpenWorkspace()
    {
        if (Detail is not { } detail)
            return;

        Failure = null;
        var connection = detail.Connection;

        if (_workspacePanes.TryGetValue(connection.Id, out var already)
            && Workspace.Pane(already) is not null)
        {
            Workspace.FocusPane(already);
            Show();
            return;
        }

        var pane = new Pane
        {
            Title = $"{connection.Name} workspace",
            Kind = new PaneKind.Workspace(),
            ConnectionId = connection.Id,
        };

        try
        {
            // Off the UI thread for the same reason the browser is: SFTP costs
            // its own authentication, and a window that freezes while a host
            // times out is what that would cost.
            var files = await Task.Run(() => _sessions.Files(connection));
            var model = new HostWorkspaceViewModel(
                files,
                connection.Name,
                _roots.GetValueOrDefault(connection.Id),
                _dialogs,
                remember: root =>
                {
                    _roots[connection.Id] = root;
                    WorkspacePreferences.Save(_preferencesPath, connection.Id, root);
                });

            // The conversation about this host has to notice: the folder decides
            // which tools it is offered, and it may already be open in the dock.
            model.RootChanged += (_, _) => _conversations.GetValueOrDefault(connection.Id)?.WorkspaceChanged();
            model.Saveability += (_, _) =>
            {
                OnPropertyChanged(nameof(CanSaveFile));
                SaveFileCommand.NotifyCanExecuteChanged();
            };

            _workspaces[connection.Id] = model;
            _workspacePanes[connection.Id] = pane.Id;

            Workspace.Open(pane, Panes.Count > 0 ? Workspace.ActiveTab?.Axis ?? SplitAxis.Horizontal : null);
            _views[pane.Id] = _workspaceView(model);
            Show();
            _conversations.GetValueOrDefault(connection.Id)?.WorkspaceChanged();
            await model.Refresh();
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"opening a workspace on {connection.Name} failed: {e}");
            Failure = SshFailure.Classify(e).Summary;
        }
    }

    /// <summary>How a workspace becomes something on screen. Replaced in tests.</summary>
    private readonly Func<HostWorkspaceViewModel, Control> _workspaceView;

    /// <summary>How the editor half alone becomes something on screen. Likewise.</summary>
    private readonly Func<HostWorkspaceViewModel, Control> _editorView;

    /// <summary>The pane this machine's files are edited in, once one is open.</summary>
    private NodeId? _editorPane;

    /// <summary>
    /// Puts the local editor on screen, or brings it forward.
    ///
    /// Opening a file in the sidebar is what asks for this: the tree is 260
    /// pixels wide and a line of code is not, so the document goes where the
    /// sessions are. One pane however many files are open -- they are tabs
    /// inside it, as an editor has them.
    /// </summary>
    private void OpenLocalEditor()
    {
        if (LocalFiles.Current is null)
            return;

        if (_editorPane is { } already && Workspace.Pane(already) is not null)
        {
            Workspace.FocusPane(already);
            Show();
            return;
        }

        var pane = new Pane
        {
            Title = "Files",
            Kind = new PaneKind.Editor(),
            // No host: these files are this machine's, so disconnecting
            // anything must not close them.
            ConnectionId = null,
        };

        _editorPane = pane.Id;
        Workspace.Open(pane, Panes.Count > 0 ? Workspace.ActiveTab?.Axis ?? SplitAxis.Horizontal : null);
        _views[pane.Id] = _editorView(LocalFiles);
        Show();
    }

    /// <summary>
    /// Copies a file from this machine into the folder a host has open.
    ///
    /// The sentence this exists for is "get this file onto that server", and
    /// until now it needed the host's own pane, its upload button and a file
    /// picker pointed back at the folder you were already looking at.
    ///
    /// Into the host workspace's own root, which means the same rule applies:
    /// the destination is a folder somebody deliberately opened.
    /// </summary>
    private async Task SendToHost(WorkspaceNode node)
    {
        if (Sending is not { } destination)
            return;

        if (LocalFiles.Workspace is not { } here)
            return;

        var (host, workspace) = destination;
        var local = here.Resolve(node.Relative);

        await LocalFiles.Copying(
            $"Sending {node.Name} to {host}",
            () => workspace.Upload(Transport.LocalFiles.Native(local), node.Name),
            $"Sent {node.Name} to {host}");
    }

    /// <summary>
    /// The one host with a folder open, and its workspace.
    ///
    /// One rather than a choice: with two open there is no answer to "the open
    /// host" that is not a guess, so the menu item says which one it means and
    /// disappears when the question is ambiguous.
    /// </summary>
    private (string Host, RemoteWorkspace Workspace)? Sending =>
        _workspaces.Count == 1
            && Inventory.Tree.Connections.GetValueOrDefault(_workspaces.Keys.Single()) is { } connection
            && _workspaces.Values.Single().Workspace is { } open
            ? (connection.Name, open)
            : null;

    /// <summary>Keeps the sidebar's menu item naming the host it would send to.</summary>
    private void RefreshSendTarget() => LocalFiles.SendTarget = Sending?.Host ?? "";

    /// <summary>Which folder each host was last opened at, remembered between runs.</summary>
    private readonly Dictionary<NodeId, string> _roots;

    /// <summary>
    /// The workspace of whichever pane has the keyboard, for ⌘S.
    ///
    /// The focused pane rather than "the only one open": with a workspace on two
    /// hosts side by side, saving has to mean the file you are looking at.
    /// </summary>
    private HostWorkspaceViewModel? FocusedWorkspace => Workspace.ActivePane switch
    {
        { Kind: PaneKind.Workspace, ConnectionId: { } host } => _workspaces.GetValueOrDefault(host),
        // The editor pane holds this machine's files, and ⌘S there means the
        // file in front of you exactly as it does in a host's folder.
        { Kind: PaneKind.Editor } => LocalFiles,
        _ => null,
    };

    public bool CanSaveFile => FocusedWorkspace?.CanSave == true;

    /// <summary>Writes the focused workspace's open file back to its host.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveFile))]
    public async Task SaveFile()
    {
        if (FocusedWorkspace is { } workspace)
            await workspace.Save();
    }

    /// <summary>Opens the selected host's tunnels, beside whatever is open.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedHost))]
    public async Task OpenTunnels()
    {
        if (Detail is not { } detail)
            return;

        Failure = null;
        var connection = detail.Connection;
        var forwards = Inventory.Tree.Resolve(connection.Id).Settings.PortForwards;
        var pane = new Pane
        {
            Title = $"{connection.Name} tunnels",
            Kind = new PaneKind.Tunnels(),
            ConnectionId = connection.Id,
        };

        try
        {
            // Connecting is what takes the time; the forwards themselves are
            // already known, and none is started until someone asks.
            var tunnels = await Task.Run(() => _sessions.Tunnels(connection));
            var model = new TunnelsViewModel(tunnels, connection.Name, forwards);

            Workspace.Open(pane, Panes.Count > 0 ? Workspace.ActiveTab?.Axis ?? SplitAxis.Horizontal : null);
            _views[pane.Id] = _tunnelsView(model);
            Show();
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine($"opening tunnels for {connection.Name} failed: {e}");
            Failure = SshFailure.Classify(e).Summary;
        }
    }

    /// <summary>How a set of tunnels becomes something on screen. Replaced in tests.</summary>
    private readonly Func<TunnelsViewModel, Control> _tunnelsView = model => new TunnelsView(model);

    /// <summary>
    /// Shows the assistant in the dock, on the selected host.
    ///
    /// Docked rather than split into the sessions: the middle of the window is
    /// for the servers, and a conversation that took a tile from them would make
    /// every session smaller to say one thing about one of them. It still sits
    /// beside its subject, because in a tiled window every session is beside it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedHost))]
    public void OpenAssistant()
    {
        if (Detail is not { } detail)
            return;

        Failure = null;
        if (_backends(AssistantSettings) is null)
        {
            // No key is an ordinary state on a fresh install. It deserves a
            // sentence offering Settings, not a dock that fails when asked.
            Failure = AssistBackends.NoKey(AssistantSettings);
            return;
        }

        DockShows = DockView.Assistant;
        IsDockOpen = true;
        ShowInDock(detail.Connection);
    }

    /// <summary>
    /// This host's conversation, built on first use and kept for as long as the
    /// window is open.
    ///
    /// Kept rather than rebuilt, because the dock follows the focus: clicking
    /// back to a session you asked about ten minutes ago has to find what it
    /// said, or the dock would be a conversation that forgets every time you
    /// look at another server.
    /// </summary>
    private readonly Dictionary<NodeId, Control> _assistants = [];

    private void ShowInDock(Connection connection)
    {
        if (!_assistants.TryGetValue(connection.Id, out var view))
        {
            AssistantViewModel? model = null;
            model = new AssistantViewModel(
                // Whichever of this host's shells has the keyboard now, asked
                // each time rather than captured: the dock outlives the pane it
                // was opened from.
                Agent(connection, () => Focused(connection.Id))(() => model!),
                AssistantSettings,
                // Typed into the shell it is about, and not run. Sent rather
                // than written, so a broadcast group fans it out as it fans out
                // typing.
                text => (Focused(connection.Id) is { } id ? _terminals.Session(id) : ActiveTerminal())?.Send(text),
                // What the header names and the Edit files switch depends on.
                () => _workspaces.GetValueOrDefault(connection.Id)?.Workspace?.RootLabel,
                () => LocalFiles.Workspace?.RootLabel,
                // What /mcp answers with, read out of the hub rather than asked
                // of a model that cannot see a server which failed to start.
                () => ConnectedToolsReport.Of(_tools))
            {
                // The dock's own header names the host, and at this width two
                // labels for it leave neither any room.
                ShowsAlias = false,
            };

            // Said in the pane showing this host and marked on its tile, for as
            // long as it has it -- the same path an orchestrated run takes.
            model.Driving += (_, step) => Mark(step);
            // A file the assistant wrote is a file the pane is still showing the
            // old version of. Showing a stale file is worse than showing none,
            // because it looks current.
            model.Wrote += async (_, change) =>
            {
                if (_workspaces.GetValueOrDefault(connection.Id) is { } workspace)
                    await workspace.Changed(change);
            };
            // The same for a file on this machine, which the sidebar and the
            // editor pane are both showing.
            model.WroteHere += async (_, change) => await LocalFiles.Changed(change);

            _conversations[connection.Id] = model;
            _assistants[connection.Id] = view = _assistantView(model);

            // The disclosure is filled in before anything is typed: every
            // question shows exactly what it will carry before you ask it.
            _ = model.LookCommand.ExecuteAsync(null);
        }

        Dock = view;
        DockTitle = connection.Name;
    }

    /// <summary>
    /// Points the dock at whatever now has the focus.
    ///
    /// Only while it is open and showing an assistant: the orchestrator is about
    /// several hosts and has no focus to follow, and building a conversation for
    /// every session someone clicks through would be a provider call each time.
    /// </summary>
    private void RefreshDock()
    {
        if (!IsDockOpen || DockShows != DockView.Assistant)
            return;
        if (Workspace.ActivePane?.ConnectionId is not { } host)
            return;
        if (Inventory.Tree.Connections.GetValueOrDefault(host) is not { } connection)
            return;
        if (_backends(AssistantSettings) is null)
            return;
        ShowInDock(connection);
    }

    /// <summary>How an assistant becomes something on screen. Replaced in tests.</summary>
    private readonly Func<AssistantViewModel, Control> _assistantView;

    /// <summary>
    /// Shows the orchestrator in the dock: one instruction across several hosts.
    ///
    /// Built once and kept, and belonging to no host, because disconnecting a
    /// host closes what belongs to it and a fan-out across eight servers should
    /// not vanish because one was disconnected.
    /// </summary>
    [RelayCommand]
    public void AskSeveralHosts()
    {
        Failure = null;
        if (_backends(AssistantSettings) is not { } backend)
        {
            Failure = AssistBackends.NoKey(AssistantSettings);
            return;
        }

        DockShows = DockView.Orchestrator;
        IsDockOpen = true;
        DockTitle = "";

        if (_orchestrator is not null)
        {
            Dock = _orchestrator;
            return;
        }

        OrchestratorViewModel? model = null;
        model = new OrchestratorViewModel(
            backend,
            Inventory.Tree.Connections.Values
                .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
                .Select(connection => new TargetRow
                {
                    Alias = connection.Name,
                    // Shown, not required. A ticked host that is not open is
                    // connected when the run reaches it; this only says which
                    // ones already are.
                    IsConnected = _sessions.IsOpen(connection),
                }),
            alias => Known(alias) is { } target
                // No terminal, deliberately. An orchestrated run is not about
                // the pane you happen to have in front of you: reading one
                // would make a run across eight hosts depend on which of them
                // had a window open and what was last printed in it.
                ? Agent(target, () => null, inARun: true)(() => model!)
                : null,
            // Ask mode needs the connection and nothing else: one assistant
            // decides what to run and where, so there is no conversation per
            // host to build.
            alias => Known(alias) is { } target
                ? new SshHostAccess(
                    target.Name,
                    _sessions.Commands(target),
                    _sessions.Health(target),
                    () => null)
                : null,
            // This machine's folder, and not the hosts'. A run is where one
            // instruction becomes an action on eight servers; reading a runbook
            // here and writing down what it found is the direction that is
            // worth having, and the other one stays a job for the pane about
            // one machine.
            new RemoteWorkspaceAccess(() => LocalFiles.Workspace),
            () => LocalFiles.Workspace?.RootLabel,
            () => ConnectedToolsReport.Of(_tools));

        model.WroteHere += async (_, change) => await LocalFiles.Changed(change);

        // Which host it has, said in the pane showing that host and marked on
        // its tile, for as long as it has it. The command itself still goes
        // over the exec channel; this is only the window saying so.
        model.Driving += (_, step) => Mark(step);

        Dock = _orchestrator = _orchestratorView(model);
    }

    /// <summary>
    /// Marks every open pane for a host while the assistant is working in it.
    ///
    /// Every pane rather than the focused one: a host can be open twice, and
    /// marking one of them would be telling half the truth. Nothing at all
    /// where a host has no pane, which is the ordinary case for a run over a
    /// rack -- and the run does not depend on it either way.
    /// </summary>
    private void Mark(AssistStep step)
    {
        var panes = Workspace.Panes
            .Where(pane => pane.IsTerminal && Named(pane.ConnectionId) == step.Host)
            .Select(pane => pane.Id)
            .ToArray();

        foreach (var pane in panes)
            _terminals.Show(pane, step.Running ? Narration.Starting(step) : Narration.Finished(step));

        Driving = step.Running ? panes : [];
    }

    /// <summary>What the inventory calls the host a pane belongs to.</summary>
    private string? Named(NodeId? connection) =>
        connection is { } id ? Inventory.Tree.Connections.GetValueOrDefault(id)?.Name : null;

    /// <summary>
    /// The panes the assistant has hold of just now.
    ///
    /// The layout outlines them while it does, which is the whole of "watch it
    /// work": a tile that is being driven says so, and stops saying so the
    /// moment the command comes back.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<NodeId> Driving { get; private set; } = [];

    /// <summary>The fan-out, once someone has asked for it. One per window.</summary>
    private Control? _orchestrator;

    /// <summary>How an orchestrator becomes something on screen. Replaced in tests.</summary>
    private readonly Func<OrchestratorViewModel, Control> _orchestratorView;

    /// <summary>
    /// Builds a conversation about one host.
    ///
    /// The gate is passed in late because a pane is its own gate and cannot be
    /// built before the agent it holds.
    /// </summary>
    /// <param name="terminal">
    /// Which shell's tail the question carries, asked for at the time rather
    /// than passed in: a docked assistant follows the focus, so the pane it is
    /// about is not known when it is built.
    /// </param>
    private Func<Func<ICommandGate>, HostAgent> Agent(Connection connection, Func<NodeId?> terminal, bool inARun = false) =>
        gate =>
        {
            var access = new SshHostAccess(
                connection.Name,
                _sessions.Commands(connection),
                _sessions.Health(connection),
                TerminalTail.Of(_terminals, terminal, AssistantSettings.TerminalTailLines));

            return new HostAgent(
                _backends(AssistantSettings)
                    ?? throw new AssistException(AssistBackends.NoKey(AssistantSettings)),
                access,
                AssistantSettings,
                new Deferred(gate),
                ToolsFor(inARun),
                // Asked for each time rather than captured: a folder is opened
                // and closed while a conversation is going on, and the tools
                // appear and go with it.
                new RemoteWorkspaceAccess(() => _workspaces.GetValueOrDefault(connection.Id)?.Workspace),
                // And this machine's folder, which belongs to no host and is the
                // same one in every conversation.
                new RemoteWorkspaceAccess(() => LocalFiles.Workspace));
        };

    /// <summary>
    /// A host by the name the inventory gives it, open or not.
    ///
    /// Not "and only if it is connected", which is what this was. The session
    /// pool connects on first use, so a run reaches a ticked host whether or not
    /// somebody opened a terminal on it first -- and a host that cannot be
    /// reached says why, in its own row, rather than the run quietly leaving it
    /// out. The cost is that connecting can still raise a host-key decision, and
    /// that is a question only a person can answer.
    /// </summary>
    private Connection? Known(string alias) =>
        Inventory.Tree.Connections.Values.FirstOrDefault(connection => connection.Name == alias);

    /// <summary>The host's focused shell, when one of its panes has the keyboard.</summary>
    private NodeId? Focused(NodeId host) =>
        Workspace.ActivePane is { IsTerminal: true, ConnectionId: var owner, Id: var pane } && owner == host
            ? pane
            : Workspace.Panes.FirstOrDefault(open => open.IsTerminal && open.ConnectionId == host)?.Id;

    /// <summary>
    /// A gate that is not known until the pane holding it exists.
    ///
    /// The pane answers the gate and the pane holds the agent, so one of the two
    /// references has to be late. This is it.
    /// </summary>
    private sealed class Deferred(Func<ICommandGate> gate) : ICommandGate
    {
        public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default) =>
            gate().Allow(command, cancellationToken);

        /// <summary>
        /// Forwarded too. A gate that passed on only the command half would let
        /// the interface's default answer -- no -- silently refuse every
        /// connected tool call, which looks exactly like a person refusing.
        /// </summary>
        public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default) =>
            gate().Allow(call, cancellationToken);
    }

    /// <summary>Opens a shell on a host, in its own tab.</summary>
    public async Task OpenTerminal(Connection connection, SplitAxis? splitting = null)
    {
        Failure = null;
        var pane = new Pane { Title = connection.Name, Kind = new PaneKind.Terminal(), ConnectionId = connection.Id };

        try
        {
            // Connecting blocks on the network, so it happens off the UI thread;
            // a window that freezes while a host times out is the thing this
            // avoids.
            var session = await Task.Run(() => _sessions.Shell(connection));
            // Registered here rather than by the view: broadcast and snippets ask
            // the registry which shell has the focus, and that must not depend on
            // which control was built for the pane.
            _terminals.Register(session);
            var view = _view(session, _theme.TerminalPaletteFor(Inventory.Tree, connection));

            session.TitleChanged += (_, title) => Dispatcher.UIThread.Post(() => Workspace.UpdateTitle(session.Id, title));
            session.Ended += (_, _) => Dispatcher.UIThread.Post(() => Workspace.PaneExited(session.Id, null));

            Workspace.Open(pane with { Id = session.Id }, splitting);
            _views[session.Id] = view;
            Show();
        }
        catch (Exception e)
        {
            // A failed connection says why, where the user was looking -- and the
            // whole exception goes to the trace, because a one-line summary is
            // not enough to fix anything by.
            System.Diagnostics.Trace.WriteLine($"opening {connection.Name} failed: {e}");
            Failure = SshFailure.Classify(e).Summary;
        }
    }

    private readonly Dictionary<NodeId, Control> _views = [];

    /// <summary>
    /// Repaints every open terminal, in place. A pane whose host names a theme
    /// of its own keeps it, so this asks for each pane's colours rather than
    /// handing out the same ones.
    /// </summary>
    private void Recolour()
    {
        foreach (var pane in Workspace.Panes)
        {
            if (_views.GetValueOrDefault(pane.Id) is not IThemedPane themed)
                continue;
            if (pane.ConnectionId is { } host && Inventory.Tree.Connections.GetValueOrDefault(host) is { } connection)
                themed.Apply(_theme.TerminalPaletteFor(Inventory.Tree, connection));
            else
                themed.Apply(_theme.Palette.Terminal);
        }
    }

    private void CloseEverythingFor(NodeId host)
    {
        foreach (var pane in Workspace.Panes.Where(pane => pane.ConnectionId == host).Select(pane => pane.Id).ToArray())
            Workspace.ClosePane(pane);
        Show();
    }

    /// <summary>
    /// Tells every command to ask again whether it can run.
    ///
    /// A menu greys an item out by asking, and asks only when told the answer may
    /// have changed. Selecting a host and closing the last pane are the two
    /// moments that change it.
    /// </summary>
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(HasSelectedHost));
        OnPropertyChanged(nameof(HasOpenPane));
        OnPropertyChanged(nameof(CanBroadcast));
        OnPropertyChanged(nameof(CanSaveFile));

        ConnectSelectedCommand.NotifyCanExecuteChanged();
        EditSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SplitRightCommand.NotifyCanExecuteChanged();
        SplitDownCommand.NotifyCanExecuteChanged();
        ClosePaneCommand.NotifyCanExecuteChanged();
        BrowseFilesCommand.NotifyCanExecuteChanged();
        OpenWorkspaceCommand.NotifyCanExecuteChanged();
        SaveFileCommand.NotifyCanExecuteChanged();
        RunSnippetCommand.NotifyCanExecuteChanged();
        OpenTunnelsCommand.NotifyCanExecuteChanged();
        OpenAssistantCommand.NotifyCanExecuteChanged();
        ToggleBroadcastCommand.NotifyCanExecuteChanged();
        FocusTabAtCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Drops the views of panes that are gone, whichever way they went — a pane
    /// closed, a tab closed, a host deleted. One place rather than three, and the
    /// only place a pane view is disposed.
    /// </summary>
    private void PruneViews()
    {
        var open = Workspace.Panes.Select(pane => pane.Id).ToHashSet();

        // Closing the pane closes the folder, which takes the assistant's file
        // tools with it. That is the whole contract: what it may touch is what
        // you have open in front of you.
        foreach (var (host, pane) in _workspacePanes.Where(pane => !open.Contains(pane.Value)).ToArray())
        {
            _workspacePanes.Remove(host);
            _workspaces.Remove(host);
            _conversations.GetValueOrDefault(host)?.WorkspaceChanged();
        }

        foreach (var (id, view) in _views.Where(pane => !open.Contains(pane.Key)).ToArray())
        {
            // Whatever the pane holds — an ssh session, a running forward — goes
            // with it. The view knows what it has; this only says when.
            (view as IDisposable)?.Dispose();
            _views.Remove(id);
        }
    }

    private void RefreshTabs()
    {
        Tabs.Clear();
        foreach (var tab in Workspace.Tabs)
            Tabs.Add(new TabItem(tab.Id, Workspace.TitleOf(tab), tab.Id == Workspace.ActiveTabId));
    }

    /// <summary>Puts the focused tab's panes on screen, with the keyboard on one of them.</summary>
    private void Show()
    {
        PruneViews();
        RefreshTabs();
        RefreshCommands();
        // Set before Panes: the layout reads it to decide what to drop, and a
        // stale one would drop the panes of the tab being left.
        OpenPanes = [.. Workspace.Panes.Select(pane => pane.Id).Where(_views.ContainsKey)];
        // Tiled, every session is on screen at once, in the order they were
        // opened; otherwise it is the focused tab's panes, as it has always
        // been. Either way these are the same views, moved rather than remade.
        Panes = Workspace.IsTiled
            ?
            [
                .. Workspace.Panes
                    .Select(pane => (pane.Id, pane.Title, View: _views.GetValueOrDefault(pane.Id)))
                    .Where(pane => pane.View is not null)
                    .Select(pane => new PaneSlot(
                        pane.Id, pane.View!, pane.Id == Workspace.ActivePaneId, pane.Title)),
            ]
            : Workspace.ActiveTab is { } tab
                ?
                [
                    .. tab.PaneIds
                        .Select(id => (Id: id, View: _views.GetValueOrDefault(id)))
                        .Where(pane => pane.View is not null)
                        .Select(pane => new PaneSlot(pane.Id, pane.View!, pane.Id == tab.ActivePaneId)),
                ]
                : [];

        RefreshDock();
        RefreshSendTarget();

        OnPropertyChanged(nameof(Axis));
        OnPropertyChanged(nameof(IsTiled));
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPanes));
        OnPropertyChanged(nameof(ShowsEmptyState));
        OnPropertyChanged(nameof(CanBroadcast));

        // Switching back to a tab has to hand the keyboard back too: the pane was
        // loaded long ago, so nothing else will.
        if (Panes.FirstOrDefault(pane => pane.IsActive)?.View is TerminalPaneView terminal)
            Dispatcher.UIThread.Post(terminal.FocusTerminal, DispatcherPriority.Input);
    }
}
