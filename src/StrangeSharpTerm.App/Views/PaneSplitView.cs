using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// The focused tab's panes, side by side or stacked, with a draggable divider
/// between them.
///
/// Built in code rather than in markup because the number of panes is not known
/// until a tab is split: a Grid's rows and columns have to be made, not
/// declared.
///
/// It keeps one grid and one frame per pane for as long as that pane is open,
/// and moves them between cells. Rebuilding instead — a fresh grid each time the
/// list changes — is what the first attempt did, and it detached every pane and
/// re-attached it. That is not a redraw: a terminal control that leaves the
/// visual tree tears its connection down, and both panes ended up reading one
/// stream, each seeing half of it. A pane's frame is therefore made once, and
/// only what actually changed is changed.
///
/// <see cref="Panes"/> is the focused tab's panes, which is why it cannot decide
/// on its own what to take out of the grid: a pane missing from it has either
/// closed or is sitting in another tab, and the two must not be treated alike.
/// <see cref="OpenPanes"/> is what tells them apart. Getting this wrong was the
/// same bug one level up: opening a second tab detached the first tab's panes,
/// the control tore its connection down — "Process exited with code: 0", our
/// ExitCode — and on the way back it launched a default process of its own. The
/// tab returned showing a local shell wearing the remote host's name.
/// </summary>
public sealed class PaneSplitView : Decorator
{
    public static readonly StyledProperty<IReadOnlyList<PaneSlot>?> PanesProperty =
        AvaloniaProperty.Register<PaneSplitView, IReadOnlyList<PaneSlot>?>(nameof(Panes));

    /// <summary>
    /// Every pane that is open, across every tab — not just the focused tab's.
    ///
    /// Null means there are no other tabs to account for, so the focused tab is
    /// the whole world and a pane missing from it has genuinely closed.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<NodeId>?> OpenPanesProperty =
        AvaloniaProperty.Register<PaneSplitView, IReadOnlyList<NodeId>?>(nameof(OpenPanes));

    public static readonly StyledProperty<SplitAxis> AxisProperty =
        AvaloniaProperty.Register<PaneSplitView, SplitAxis>(nameof(Axis));

    /// <summary>Told which pane the pointer went into, so the keyboard can follow.</summary>
    public static readonly StyledProperty<ICommand?> FocusCommandProperty =
        AvaloniaProperty.Register<PaneSplitView, ICommand?>(nameof(FocusCommand));

    private readonly Grid _grid = new();
    private readonly Dictionary<NodeId, Border> _frames = [];

    static PaneSplitView()
    {
        PanesProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Arrange());
        OpenPanesProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Arrange());
        AxisProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Arrange());
    }

    public PaneSplitView() => Child = _grid;

    public IReadOnlyList<PaneSlot>? Panes
    {
        get => GetValue(PanesProperty);
        set => SetValue(PanesProperty, value);
    }

    public IReadOnlyList<NodeId>? OpenPanes
    {
        get => GetValue(OpenPanesProperty);
        set => SetValue(OpenPanesProperty, value);
    }

    public SplitAxis Axis
    {
        get => GetValue(AxisProperty);
        set => SetValue(AxisProperty, value);
    }

    public ICommand? FocusCommand
    {
        get => GetValue(FocusCommandProperty);
        set => SetValue(FocusCommandProperty, value);
    }

    private void Arrange()
    {
        var panes = Panes ?? [];
        var sideBySide = Axis == SplitAxis.Horizontal;

        // Panes that have closed: the frame goes, and the view with it — whoever
        // closed the pane owns disposing what was inside. Closed means gone from
        // every tab, not merely absent from this one; OpenPanes is the only thing
        // that knows the difference, and without it the focused tab is all there is.
        var open = OpenPanes;
        var closed = _frames
            .Where(pane => open is null ? panes.All(shown => shown.Id != pane.Key) : !open.Contains(pane.Key))
            .ToArray();
        foreach (var (id, frame) in closed)
        {
            frame.Child = null;
            _grid.Children.Remove(frame);
            _frames.Remove(id);
        }

        // Whatever survives that belongs to some tab. The ones this tab is not
        // showing are hidden where they stand rather than taken out of the grid:
        // an invisible control is still in the visual tree, and a terminal that
        // leaves the tree loses the connection it was given.
        foreach (var frame in _frames.Values)
            frame.IsVisible = false;

        // Dividers carry no state, so they are made fresh each time rather than
        // matched up.
        foreach (var divider in _grid.Children.OfType<GridSplitter>().ToArray())
            _grid.Children.Remove(divider);

        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        if (panes.Count == 0)
            return;

        for (var index = 0; index < panes.Count; index++)
        {
            // Every pane an equal share, with a divider between them. Dragging
            // one changes these; opening another resets them, as a fresh tab is.
            if (index > 0)
                Track(GridLength.Auto);
            Track(new GridLength(1, GridUnitType.Star));
        }

        for (var index = 0; index < panes.Count; index++)
        {
            var slot = panes[index];
            var frame = Frame(slot);
            frame.IsVisible = true;

            // The focused pane is outlined. With one pane it says little; with
            // three it is the only way to know where a keystroke goes.
            frame.Classes.Set("active", slot.IsActive);

            // Both are set every time: switching axis leaves the other one behind.
            Grid.SetColumn(frame, sideBySide ? index * 2 : 0);
            Grid.SetRow(frame, sideBySide ? 0 : index * 2);

            if (index == 0)
                continue;

            var divider = new GridSplitter
            {
                Classes = { "pane" },
                ResizeDirection = sideBySide ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                HorizontalAlignment = sideBySide ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                VerticalAlignment = sideBySide ? VerticalAlignment.Stretch : VerticalAlignment.Center,
            };
            // Its own track is Auto, so it needs a size of its own: without one
            // it measures to nothing and cannot be grabbed.
            if (sideBySide)
                divider.Width = 6;
            else
                divider.Height = 6;

            Grid.SetColumn(divider, sideBySide ? index * 2 - 1 : 0);
            Grid.SetRow(divider, sideBySide ? 0 : index * 2 - 1);
            _grid.Children.Add(divider);
        }

        void Track(GridLength length)
        {
            if (sideBySide)
                _grid.ColumnDefinitions.Add(new ColumnDefinition(length));
            else
                _grid.RowDefinitions.Add(new RowDefinition(length));
        }
    }

    /// <summary>This pane's frame, made once and kept while the pane is open.</summary>
    private Border Frame(PaneSlot slot)
    {
        if (_frames.TryGetValue(slot.Id, out var existing))
            return existing;

        var frame = new Border { Classes = { "pane" }, Child = slot.View };

        // Tunnelling, so the click reaches here before the terminal takes it: the
        // terminal wants the keyboard, and this wants to know which pane asked.
        frame.AddHandler(
            PointerPressedEvent,
            (_, _) =>
            {
                if (FocusCommand?.CanExecute(slot.Id) == true)
                    FocusCommand.Execute(slot.Id);
            },
            RoutingStrategies.Tunnel);

        _frames[slot.Id] = frame;
        _grid.Children.Add(frame);
        return frame;
    }
}
