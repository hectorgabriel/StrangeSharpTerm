using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Terminal;
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

    /// <param name="connect">How a host becomes a session. Replaced in tests by something that needs no server.</param>
    /// <param name="view">How a session becomes something on screen. Likewise.</param>
    public ShellViewModel(
        InventoryViewModel inventory,
        Func<Connection, TerminalSession>? connect = null,
        Func<TerminalSession, TerminalPalette, Control>? view = null)
    {
        Inventory = inventory;
        Workspace = new WorkspaceViewModel(_terminals);
        _connect = connect ?? (connection => TerminalLauncher.Connect(Inventory.Tree, connection));
        _view = view ?? ((session, palette) => new TerminalPaneView(session, palette, _terminals));

        // Focusing a pane moves the sidebar with it, and deleting a host closes
        // whatever it had open. Neither half knows about the other.
        Workspace.HostFocused += (_, host) => Inventory.Selection = host;
        Inventory.ConnectionRemoving += (_, host) => CloseEverythingFor(host);
        Workspace.PropertyChanged += (_, _) => RefreshTabs();
    }

    public InventoryViewModel Inventory { get; }

    public WorkspaceViewModel Workspace { get; }

    public ObservableCollection<TabItem> Tabs { get; } = [];

    /// <summary>The focused pane's view, or a message when there is nothing to show.</summary>
    [ObservableProperty]
    public partial Control? PaneContent { get; private set; }

    /// <summary>What went wrong with the last connection attempt, for the window to show.</summary>
    [ObservableProperty]
    public partial string? Failure { get; private set; }

    /// <summary>A row was activated: a folder opens or shuts, a host connects.</summary>
    [RelayCommand]
    public async Task Activate(SidebarRow? row)
    {
        if (row is null)
            return;
        if (row.IsFolder)
        {
            Inventory.Toggle(row.Id);
            return;
        }

        Inventory.Selection = row.Id;
        if (Inventory.Tree.Connections.GetValueOrDefault(row.Id) is { } connection)
            await OpenTerminal(connection);
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
            var palette = TerminalPalette.ByName(Inventory.Tree.Resolve(connection.Id).Settings.TerminalTheme);
            var view = _view(session, palette);

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
        if (PaneContent is TerminalPaneView terminal)
            Dispatcher.UIThread.Post(terminal.FocusTerminal, DispatcherPriority.Input);
    }
}
