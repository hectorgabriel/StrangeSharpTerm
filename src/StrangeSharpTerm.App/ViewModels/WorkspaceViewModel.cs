using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// The tabs, their panes, and which one has focus.
///
/// The second seam out of the Swift god object. It holds no terminals itself —
/// those live in the registry, keyed by pane — so the arrangement can be tested
/// without a server or a window.
/// </summary>
public sealed partial class WorkspaceViewModel(TerminalRegistry? terminals = null) : ObservableObject
{
    private readonly TerminalRegistry? _terminals = terminals;

    public List<Pane> Panes { get; } = [];

    public List<WorkspaceTab> Tabs { get; } = [];

    [ObservableProperty]
    public partial NodeId? ActiveTabId { get; set; }

    public WorkspaceTab? ActiveTab => Tabs.FirstOrDefault(tab => tab.Id == ActiveTabId);

    /// <summary>The pane with focus, in the tab with focus.</summary>
    public Pane? ActivePane => ActiveTab is { } tab ? Pane(tab.ActivePaneId) : null;

    public NodeId? ActivePaneId => ActiveTab?.ActivePaneId;

    /// <summary>Asked for when focus moves to a pane about a host, so the sidebar can follow.</summary>
    public event EventHandler<NodeId>? HostFocused;

    public Pane? Pane(NodeId id) => Panes.FirstOrDefault(pane => pane.Id == id);

    /// <summary>The tab's title: the focused pane's, with a count once it splits.</summary>
    public string TitleOf(WorkspaceTab tab)
    {
        var title = Pane(tab.ActivePaneId)?.Title ?? "Session";
        return tab.PaneIds.Count > 1 ? $"{title} +{tab.PaneIds.Count - 1}" : title;
    }

    /// <summary>
    /// Adds a pane: in a new tab, or splitting the focused one along an axis.
    /// </summary>
    public void Open(Pane pane, SplitAxis? splitting = null)
    {
        Panes.Add(pane);

        if (splitting is { } axis && ActiveTab is { } tab)
        {
            tab.PaneIds.Add(pane.Id);
            tab.Axis = axis;
            tab.ActivePaneId = pane.Id;
            // A new pane joins the group, or the toggle would silently exclude it.
            SyncBroadcastGroup();
        }
        else
        {
            var opened = new WorkspaceTab { PaneIds = [pane.Id], ActivePaneId = pane.Id };
            Tabs.Add(opened);
            ActiveTabId = opened.Id;
        }
        OnPropertyChanged(nameof(Tabs));
    }

    public void UpdateTitle(NodeId pane, string title)
    {
        if (title.Length == 0 || Pane(pane) is not { } found)
            return;
        // Changed in place rather than replaced: a new record would mint a new id
        // and break tab selection and closing, which key off it.
        found.Title = title;
        OnPropertyChanged(nameof(Tabs));
    }

    public void PaneExited(NodeId pane, int? code)
    {
        if (Pane(pane) is not { } found)
            return;
        found.HasExited = true;
        found.ExitCode = code;
        OnPropertyChanged(nameof(Tabs));
    }

    /// <summary>Closes one pane, and the tab with it when it was the last one.</summary>
    public void ClosePane(NodeId id)
    {
        Panes.RemoveAll(pane => pane.Id == id);
        // Closing a pane must end what it owns, or a session keeps running with
        // nothing on screen to show for it.
        _terminals?.Session(id)?.Dispose();
        _terminals?.Forget(id);

        var tab = Tabs.FirstOrDefault(candidate => candidate.PaneIds.Contains(id));
        if (tab is null)
            return;

        tab.PaneIds.Remove(id);
        if (tab.PaneIds.Count == 0)
        {
            Tabs.Remove(tab);
            if (ActiveTabId == tab.Id)
                ActiveTabId = Tabs.LastOrDefault()?.Id;
        }
        else if (tab.ActivePaneId == id)
        {
            tab.ActivePaneId = tab.PaneIds[^1];
        }
        SyncBroadcastGroup();
        OnPropertyChanged(nameof(Tabs));
    }

    public void CloseTab(NodeId id)
    {
        if (Tabs.FirstOrDefault(tab => tab.Id == id) is not { } tab)
            return;
        foreach (var pane in tab.PaneIds.ToArray())
            ClosePane(pane);
    }

    public void FocusPane(NodeId id)
    {
        if (Tabs.FirstOrDefault(tab => tab.PaneIds.Contains(id)) is not { } tab)
            return;

        tab.ActivePaneId = id;
        ActiveTabId = tab.Id;
        // An orchestrator pane is about no one host, so it leaves the sidebar
        // selection alone rather than clearing it.
        if (Pane(id)?.ConnectionId is { } host)
            HostFocused?.Invoke(this, host);
        SyncBroadcastGroup();
    }

    public void FocusTab(NodeId id)
    {
        if (Tabs.FirstOrDefault(tab => tab.Id == id) is not { } tab)
            return;
        ActiveTabId = id;
        if (Pane(tab.ActivePaneId)?.ConnectionId is { } host)
            HostFocused?.Invoke(this, host);
        SyncBroadcastGroup();
    }

    /// <summary>
    /// Focuses the nth tab, ignoring an index past the end, so pressing the
    /// shortcut for a tab that is not open does nothing rather than jumping
    /// somewhere odd.
    /// </summary>
    public void FocusTabAt(int index)
    {
        if (index >= 0 && index < Tabs.Count)
            FocusTab(Tabs[index].Id);
    }

    /// <summary>
    /// Whether every session is on screen at once, in a grid, rather than one
    /// tab's worth.
    ///
    /// It lives here rather than in the window because it decides what "every
    /// pane" means, and two things depend on that answer: what the layout draws,
    /// and where a broadcast goes.
    /// </summary>
    [ObservableProperty]
    public partial bool IsTiled { get; set; }

    partial void OnIsTiledChanged(bool value)
    {
        // The group is "what is on screen", so changing what is on screen
        // changes it. Without this, tiling while broadcasting kept typing into
        // the tab that was showing a moment ago.
        SyncBroadcastGroup();
        OnPropertyChanged(nameof(CanBroadcast));
    }

    /// <summary>Whether typing goes to every terminal pane on screen.</summary>
    [ObservableProperty]
    public partial bool IsBroadcasting { get; set; }

    partial void OnIsBroadcastingChanged(bool value) => SyncBroadcastGroup();

    /// <summary>True when enough terminals are on screen for broadcasting to mean anything.</summary>
    public bool CanBroadcast => TerminalsOnScreen().Count > 1;

    /// <summary>Keeps the registry's group in step with what is showing and the toggle.</summary>
    public void SyncBroadcastGroup() =>
        _terminals?.SetBroadcastGroup(IsBroadcasting ? TerminalsOnScreen() : []);

    /// <summary>
    /// The terminals a keystroke could reach: every one of them when tiled,
    /// otherwise the focused tab's.
    /// </summary>
    private List<NodeId> TerminalsOnScreen() =>
        IsTiled
            ? [.. Panes.Where(pane => pane.IsTerminal).Select(pane => pane.Id)]
            : ActiveTab is { } tab ? [.. tab.PaneIds.Where(id => Pane(id)?.IsTerminal == true)] : [];
}
