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
public sealed record PaneSlot(NodeId Id, Control View, bool IsActive);

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
        ISecretStore? toolTokens = null)
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

        // Focusing a pane moves the sidebar with it, and deleting a host closes
        // whatever it had open. Neither half knows about the other.
        Workspace.HostFocused += (_, host) => Inventory.Selection = host;
        Inventory.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InventoryViewModel.Selection) or nameof(InventoryViewModel.Tree))
            {
                RefreshDetail();
                OnPropertyChanged(nameof(ShowsDetail));
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

    /// <summary>Which way the focused tab's panes are laid out.</summary>
    public SplitAxis Axis => Workspace.ActiveTab?.Axis ?? SplitAxis.Horizontal;

    /// <summary>
    /// Whether the right-hand side is showing the selected host rather than a
    /// terminal: either nothing is open, or the selection is a different host
    /// from the one the focused pane belongs to.
    /// </summary>
    public bool ShowsDetail =>
        Detail is not null && (Panes.Count == 0 || Workspace.ActivePane?.ConnectionId != Inventory.Selection);

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
    /// Opens an assistant on the selected host, beside the session it is about.
    ///
    /// Beside rather than in a tab of its own: the pane is a conversation about
    /// what the terminal is showing, and a conversation that hides its subject
    /// is a worse one. It carries the tail of whichever shell has the focus, so
    /// which pane was focused when it opened is the thing it is about.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedHost))]
    public void OpenAssistant()
    {
        if (Detail is not { } detail)
            return;

        Failure = null;
        if (_backends(AssistantSettings) is not { } backend)
        {
            // No key is an ordinary state on a fresh install. It deserves a
            // sentence offering Settings, not a pane that fails when asked.
            Failure = AssistBackends.NoKey(AssistantSettings);
            return;
        }

        var connection = detail.Connection;
        var beside = Workspace.ActivePane is { IsTerminal: true, ConnectionId: var host, Id: var focused }
            && host == connection.Id
                ? focused
                : (NodeId?)null;

        var agent = Agent(connection, beside);
        var pane = new Pane
        {
            Title = $"{connection.Name} assistant",
            Kind = new PaneKind.Assistant(),
            ConnectionId = connection.Id,
        };

        // The pane is its own gate: the command is already a row in the
        // transcript by the time a person is asked, with its reason beside it.
        AssistantViewModel? model = null;
        model = new AssistantViewModel(
            agent(() => model!),
            AssistantSettings,
            // Typed into the shell it is beside, and not run. Sent rather than
            // written, so a broadcast group fans it out as it fans out typing.
            text => (beside is { } id ? _terminals.Session(id) : ActiveTerminal())?.Send(text));

        Workspace.Open(pane, Panes.Count > 0 ? Workspace.ActiveTab?.Axis ?? SplitAxis.Horizontal : null);
        _views[pane.Id] = _assistantView(model);
        Show();

        // The disclosure is filled in before anything is typed: every question
        // shows exactly what it will carry before you ask it.
        _ = model.LookCommand.ExecuteAsync(null);
    }

    /// <summary>How an assistant becomes something on screen. Replaced in tests.</summary>
    private readonly Func<AssistantViewModel, Control> _assistantView = model => new AssistantView(model);

    /// <summary>
    /// Opens the orchestrator: one instruction across several hosts.
    ///
    /// In a tab of its own and belonging to no host, because disconnecting a
    /// host closes the panes belonging to it and a fan-out across eight servers
    /// should not vanish because one was disconnected.
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

        var pane = new Pane { Title = "Orchestrator", Kind = new PaneKind.Orchestrator() };

        OrchestratorViewModel? model = null;
        model = new OrchestratorViewModel(
            backend,
            Inventory.Tree.Connections.Values
                .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
                .Select(connection => new TargetRow
                {
                    Alias = connection.Name,
                    // Only connected hosts are asked. Connecting can raise a
                    // host-key decision, and a fan-out that stopped on a dialog
                    // per host would be worse than one that says which it left out.
                    IsConnected = _sessions.IsOpen(connection),
                }),
            alias => Connected(alias) is { } target
                ? Agent(target, Focused(target.Id), inARun: true)(() => model!)
                : null);

        Workspace.Open(pane, splitting: null);
        _views[pane.Id] = _orchestratorView(model);
        Show();
    }

    /// <summary>How an orchestrator becomes something on screen. Replaced in tests.</summary>
    private readonly Func<OrchestratorViewModel, Control> _orchestratorView = model => new OrchestratorView(model);

    /// <summary>
    /// Builds a conversation about one host.
    ///
    /// The gate is passed in late because a pane is its own gate and cannot be
    /// built before the agent it holds.
    /// </summary>
    private Func<Func<ICommandGate>, HostAgent> Agent(Connection connection, NodeId? terminal, bool inARun = false) =>
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
                ToolsFor(inARun));
        };

    /// <summary>A host by the name the inventory gives it, and only if it is connected.</summary>
    private Connection? Connected(string alias) =>
        Inventory.Tree.Connections.Values.FirstOrDefault(connection => connection.Name == alias) is { } target
        && _sessions.IsOpen(target)
            ? target
            : null;

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

        ConnectSelectedCommand.NotifyCanExecuteChanged();
        EditSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SplitRightCommand.NotifyCanExecuteChanged();
        SplitDownCommand.NotifyCanExecuteChanged();
        ClosePaneCommand.NotifyCanExecuteChanged();
        BrowseFilesCommand.NotifyCanExecuteChanged();
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
        Panes = Workspace.ActiveTab is { } tab
            ?
            [
                .. tab.PaneIds
                    .Select(id => (Id: id, View: _views.GetValueOrDefault(id)))
                    .Where(pane => pane.View is not null)
                    .Select(pane => new PaneSlot(pane.Id, pane.View!, pane.Id == tab.ActivePaneId)),
            ]
            : [];

        OnPropertyChanged(nameof(Axis));
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsEmptyState));
        OnPropertyChanged(nameof(CanBroadcast));

        // Switching back to a tab has to hand the keyboard back too: the pane was
        // loaded long ago, so nothing else will.
        if (Panes.FirstOrDefault(pane => pane.IsActive)?.View is TerminalPaneView terminal)
            Dispatcher.UIThread.Post(terminal.FocusTerminal, DispatcherPriority.Input);
    }
}
