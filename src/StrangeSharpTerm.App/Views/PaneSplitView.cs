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
/// </summary>
public sealed class PaneSplitView : Decorator
{
    public static readonly StyledProperty<IReadOnlyList<PaneSlot>?> PanesProperty =
        AvaloniaProperty.Register<PaneSplitView, IReadOnlyList<PaneSlot>?>(nameof(Panes));

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
        AxisProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Arrange());
    }

    public PaneSplitView() => Child = _grid;

    public IReadOnlyList<PaneSlot>? Panes
    {
        get => GetValue(PanesProperty);
        set => SetValue(PanesProperty, value);
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
        // closed the pane owns disposing what was inside.
        foreach (var (id, frame) in _frames.Where(pane => panes.All(open => open.Id != pane.Key)).ToArray())
        {
            frame.Child = null;
            _grid.Children.Remove(frame);
            _frames.Remove(id);
        }

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
