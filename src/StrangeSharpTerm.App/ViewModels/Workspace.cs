using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>How a tab's panes are arranged.</summary>
public enum SplitAxis
{
    /// <summary>Side by side.</summary>
    Horizontal,

    /// <summary>Stacked.</summary>
    Vertical,
}

public static class SplitAxisExtensions
{
    public static SplitAxis Toggled(this SplitAxis axis) =>
        axis == SplitAxis.Horizontal ? SplitAxis.Vertical : SplitAxis.Horizontal;
}

/// <summary>
/// What a pane is showing.
///
/// Panes hold a kind rather than being terminals outright, so a file browser can
/// live alongside a shell in the same tab structure without a parallel one.
/// </summary>
public abstract record PaneKind
{
    private PaneKind() { }

    public sealed record Terminal : PaneKind;

    public sealed record Files : PaneKind;

    /// <summary>
    /// A folder on this host, open: a tree, the files being edited, and the
    /// same root the assistant may work in.
    /// </summary>
    public sealed record Workspace : PaneKind;

    /// <summary>This host's port forwards, and which of them are up.</summary>
    public sealed record Tunnels : PaneKind;

    /// <summary>A conversation about this pane's host, whose transcript lives elsewhere, keyed by pane.</summary>
    public sealed record Assistant : PaneKind;

    /// <summary>One instruction across several hosts: the only kind that belongs to no single connection.</summary>
    public sealed record Orchestrator : PaneKind;
}

/// <summary>One pane in a tab.</summary>
/// <param name="ConnectionId">
/// The host this pane is about, and null when it is about several. Optional for
/// the orchestrator alone: naming one of its hosts would be a lie with
/// consequences, because disconnecting a host closes the panes belonging to it,
/// and a fan-out across eight servers should not vanish because one was
/// disconnected.
/// </param>
public sealed record Pane
{
    public NodeId Id { get; init; } = NodeId.New();

    public NodeId? ConnectionId { get; init; }

    public required string Title { get; set; }

    public required PaneKind Kind { get; init; }

    public bool HasExited { get; set; }

    public int? ExitCode { get; set; }

    public bool IsTerminal => Kind is PaneKind.Terminal;
}

/// <summary>
/// A tab in the workspace, holding one or more panes.
///
/// Tabs own panes rather than being a session themselves, so a split is just a
/// second pane in the same tab. Modelling it the other way — a tab <em>is</em> a
/// session — makes splits impossible to express without a parallel structure.
/// </summary>
public sealed record WorkspaceTab
{
    public NodeId Id { get; init; } = NodeId.New();

    public required List<NodeId> PaneIds { get; init; }

    public SplitAxis Axis { get; set; } = SplitAxis.Horizontal;

    public required NodeId ActivePaneId { get; set; }
}
