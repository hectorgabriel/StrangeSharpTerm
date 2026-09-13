using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Terminal;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>One tab as the tab strip needs it: a name, and whether it has focus.</summary>
public sealed record TabItem(NodeId Id, string Title, bool IsActive);

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
    private readonly Func<Connection, TerminalSession> _connect;
    private readonly Func<TerminalSession, TerminalPalette, Control> _view;
    private readonly IDialogService _dialogs;
    private readonly AppTheme _theme;

    /// <param name="connect">How a host becomes a session. Replaced in tests by something that needs no server.</param>
    /// <param name="view">How a session becomes something on screen. Likewise.</param>
    public ShellViewModel(
        InventoryViewModel inventory,
        Func<Connection, TerminalSession>? connect = null,
        Func<TerminalSession, TerminalPalette, Control>? view = null,
        IDialogService? dialogs = null,
        AppTheme? theme = null)
    {
        Inventory = inventory;
        _dialogs = dialogs ?? new ScriptedDialogService();
        _theme = theme ?? new AppTheme();
        Workspace = new WorkspaceViewModel(_terminals);
        _connect = connect ?? (connection => TerminalLauncher.Connect(Inventory.Tree, connection));
        _view = view ?? ((session, palette) => new TerminalPaneView(session, palette, _terminals));

        // Focusing a pane moves the sidebar with it, and deleting a host closes
        // whatever it had open. Neither half knows about the other.
        Workspace.HostFocused += (_, host) => Inventory.Selection = host;
        Inventory.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InventoryViewModel.Selection) or nameof(InventoryViewModel.Tree))
            {
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(ShowsDetail));
                OnPropertyChanged(nameof(ShowsEmptyState));
            }
        };
        Inventory.ConnectionRemoving += (_, host) => CloseEverythingFor(host);
        Workspace.PropertyChanged += (_, _) => RefreshTabs();

        // Changing the theme repaints the open terminals where they stand. The
        // window's own colours are resources and need nobody to tell them.
        _theme.Changed += (_, _) =>
        {
            Recolour();
            OnPropertyChanged(nameof(Theme));
        };
    }

    public InventoryViewModel Inventory { get; }

    public WorkspaceViewModel Workspace { get; }

    public ObservableCollection<TabItem> Tabs { get; } = [];

    /// <summary>
    /// The theme in use. Settable, because the picker is a list of themes with
    /// one of them chosen, and that is the whole of the interaction.
    /// </summary>
    public AppPalette Theme
    {
        get => _theme.Palette;
        set => _theme.Use(value);
    }

    /// <summary>The themes there are. Two, both dark; see docs/adr/0004.</summary>
    public IReadOnlyList<AppPalette> Themes => AppPalette.BuiltIn;

    /// <summary>The focused pane's view, or a message when there is nothing to show.</summary>
    [ObservableProperty]
    public partial Control? PaneContent { get; private set; }

    /// <summary>
    /// Whether the right-hand side is showing the selected host rather than a
    /// terminal: either nothing is open, or the selection is a different host
    /// from the one the focused pane belongs to.
    /// </summary>
    public bool ShowsDetail =>
        Detail is not null && (PaneContent is null || Workspace.ActivePane?.ConnectionId != Inventory.Selection);

    /// <summary>
    /// Nothing selected and nothing open. Not simply "no detail": a terminal is
    /// showing whenever a pane is open, and an empty-state line drawn over it
    /// reads as part of the shell's output.
    /// </summary>
    public bool ShowsEmptyState => PaneContent is null && !ShowsDetail;

    /// <summary>What went wrong with the last connection attempt, for the window to show.</summary>
    [ObservableProperty]
    public partial string? Failure { get; private set; }

    /// <summary>
    /// The selected host, resolved. Null when nothing is selected, or when the
    /// selection names a host that has since been deleted.
    /// </summary>
    public HostDetailViewModel? Detail =>
        Inventory.Selection is { } id && Inventory.Tree.Connections.ContainsKey(id)
            ? new HostDetailViewModel(Inventory.Tree.Resolve(id))
            : null;

    /// <summary>Opens a shell on the selected host, for the button in the detail pane.</summary>
    [RelayCommand]
    public async Task ConnectSelected()
    {
        if (Detail is { } detail)
            await OpenTerminal(detail.Connection);
    }

    /// <summary>
    /// Deletes the selected host, after asking. The confirmation names what goes
    /// with it rather than merely asking twice.
    /// </summary>
    [RelayCommand]
    public async Task DeleteSelected()
    {
        if (Detail is not { } detail)
            return;

        Inventory.ConfirmDelete(new PendingDeletion.Connection(detail.Connection.Id));
        if (Inventory.PendingDeletionMessage is not { } message)
            return;

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
            var session = await Task.Run(() => _connect(connection));
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
        {
            Workspace.ClosePane(pane);
            _views.Remove(pane);
        }
        Show();
    }

    private void RefreshTabs()
    {
        Tabs.Clear();
        foreach (var tab in Workspace.Tabs)
            Tabs.Add(new TabItem(tab.Id, Workspace.TitleOf(tab), tab.Id == Workspace.ActiveTabId));
    }

    /// <summary>Puts the focused pane's view on screen, with the keyboard on it.</summary>
    private void Show()
    {
        RefreshTabs();
        PaneContent = Workspace.ActivePaneId is { } pane ? _views.GetValueOrDefault(pane) : null;

        // Switching back to a tab has to hand the keyboard back too: the pane was
        // loaded long ago, so nothing else will.
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsEmptyState));
        if (PaneContent is TerminalPaneView terminal)
            Dispatcher.UIThread.Post(terminal.FocusTerminal, DispatcherPriority.Input);
    }
}
