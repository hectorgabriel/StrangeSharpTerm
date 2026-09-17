using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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

    /// <summary>
    /// Every pane at once, in a grid, rather than along one axis.
    ///
    /// Four sessions become two rows of two. The arithmetic is in
    /// <see cref="Shape"/>; what matters here is that it is the same frames
    /// moved into different cells, because a terminal control that leaves the
    /// visual tree tears its connection down.
    /// </summary>
    public static readonly StyledProperty<bool> IsTiledProperty =
        AvaloniaProperty.Register<PaneSplitView, bool>(nameof(IsTiled));

    /// <summary>
    /// The panes the assistant is running a command in just now.
    ///
    /// Marked while it is, and unmarked the moment the command comes back, so
    /// a window full of servers shows which one is being worked on rather than
    /// leaving you to infer it from output appearing.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<NodeId>?> DrivingProperty =
        AvaloniaProperty.Register<PaneSplitView, IReadOnlyList<NodeId>?>(nameof(Driving));

    /// <summary>Told which pane the pointer went into, so the keyboard can follow.</summary>
    public static readonly StyledProperty<ICommand?> FocusCommandProperty =
        AvaloniaProperty.Register<PaneSplitView, ICommand?>(nameof(FocusCommand));

    /// <summary>
    /// The narrowest a tile may be before the grid gives up a column.
    ///
    /// Eighty columns of a readable monospace font, near enough. Without this a
    /// window at its minimum width answers "four sessions" with four slivers,
    /// none of which can show a command and its output.
    /// </summary>
    private const double MinimumTileWidth = 260;

    private readonly Grid _grid = new();
    private readonly Dictionary<NodeId, Border> _frames = [];

    /// <summary>
    /// Each frame's name strip: the strip itself, what it calls the pane, and
    /// the mark that says the assistant is working in it.
    /// </summary>
    private readonly Dictionary<NodeId, (Border Strip, TextBlock Name, TextBlock Mark)> _titles = [];

    /// <summary>The grid last laid out, so a resize that changes nothing costs nothing.</summary>
    private (int Rows, int Columns) _shape;

    static PaneSplitView()
    {
        PanesProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Arrange());
        OpenPanesProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Arrange());
        AxisProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Arrange());
        IsTiledProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Arrange());
        // A tiled grid is the only layout whose shape depends on how much room
        // it has, so it is the only one a resize can invalidate.
        BoundsProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Reflow());
        // Not a re-arrange: the same frames, wearing a different mark.
        DrivingProperty.Changed.AddClassHandler<PaneSplitView>((view, _) => view.Mark());
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

    public bool IsTiled
    {
        get => GetValue(IsTiledProperty);
        set => SetValue(IsTiledProperty, value);
    }

    public IReadOnlyList<NodeId>? Driving
    {
        get => GetValue(DrivingProperty);
        set => SetValue(DrivingProperty, value);
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
            // Emptied from the inside out. Detaching the frame's child alone
            // leaves the pane's own view still held by the layer between them,
            // which is a session nothing on screen refers to and nothing will
            // now dispose.
            if (frame.Child is Panel contents)
                contents.Children.Clear();
            frame.Child = null;
            _grid.Children.Remove(frame);
            _frames.Remove(id);
            _titles.Remove(id);
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
        _shape = default;
        if (panes.Count == 0)
            return;

        if (IsTiled)
        {
            Tile(panes);
            return;
        }

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
            var frame = Dress(slot);

            // All four are set every time: switching axis leaves the other
            // position behind, and coming back from a tiled grid leaves a span
            // behind that would swallow the pane beside this one.
            Grid.SetColumn(frame, sideBySide ? index * 2 : 0);
            Grid.SetRow(frame, sideBySide ? 0 : index * 2);
            Grid.SetColumnSpan(frame, 1);
            Grid.SetRowSpan(frame, 1);

            if (index == 0)
                continue;

            var divider = Divider(sideBySide);
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

    /// <summary>
    /// Every pane at once, in as square a grid as the count allows.
    ///
    /// Four sessions are two rows of two, nine are three of three, and three are
    /// two on top of one that spans the width — an empty cell would read as a
    /// pane that failed to draw rather than as arithmetic.
    /// </summary>
    private void Tile(IReadOnlyList<PaneSlot> panes)
    {
        var (rows, columns) = _shape = Shape(panes.Count);

        // Interleaved with Auto tracks for the dividers, as the single-axis
        // layout does: a star track either side of a divider that sizes itself.
        for (var column = 0; column < columns; column++)
        {
            if (column > 0)
                _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        }

        for (var row = 0; row < rows; row++)
        {
            if (row > 0)
                _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        }

        for (var index = 0; index < panes.Count; index++)
        {
            var slot = panes[index];
            var frame = Dress(slot);

            var row = index / columns;
            var column = index % columns;

            Grid.SetRow(frame, row * 2);
            Grid.SetColumn(frame, column * 2);
            // The last tile takes whatever the last row has left over, so a
            // count that does not divide leaves no hole.
            Grid.SetColumnSpan(
                frame,
                index == panes.Count - 1 ? (columns - column) * 2 - 1 : 1);
            Grid.SetRowSpan(frame, 1);
        }

        // One divider per interior boundary, spanning the whole grid: dragging
        // it moves a whole column or a whole row, which is what makes a tiled
        // window adjustable without becoming a puzzle.
        for (var column = 1; column < columns; column++)
        {
            var divider = Divider(sideBySide: true);
            Grid.SetColumn(divider, column * 2 - 1);
            Grid.SetRow(divider, 0);
            Grid.SetRowSpan(divider, rows * 2 - 1);
            _grid.Children.Add(divider);
        }

        for (var row = 1; row < rows; row++)
        {
            var divider = Divider(sideBySide: false);
            Grid.SetRow(divider, row * 2 - 1);
            Grid.SetColumn(divider, 0);
            Grid.SetColumnSpan(divider, columns * 2 - 1);
            _grid.Children.Add(divider);
        }
    }

    /// <summary>
    /// How many rows and columns a count of panes wants.
    ///
    /// As square as it can be, then narrowed until each tile clears
    /// <see cref="MinimumTileWidth"/>. Before the first layout pass there is no
    /// width to go on, and a guess of one column there would show every session
    /// stacked for a frame; an unmeasured grid is therefore left unconstrained.
    /// </summary>
    private (int Rows, int Columns) Shape(int count)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        if (Bounds.Width > 0)
            columns = Math.Min(columns, Math.Max(1, (int)(Bounds.Width / MinimumTileWidth)));
        columns = Math.Clamp(columns, 1, count);
        return ((int)Math.Ceiling(count / (double)columns), columns);
    }

    /// <summary>
    /// Lays out again when a resize changes how many columns fit, and not
    /// otherwise: every pointer move over a dragged splitter raises Bounds.
    /// </summary>
    private void Reflow()
    {
        if (!IsTiled || Panes is not { Count: > 0 } panes || Shape(panes.Count) == _shape)
            return;
        Arrange();
    }

    private static GridSplitter Divider(bool sideBySide) => new()
    {
        Classes = { "pane" },
        ResizeDirection = sideBySide ? GridResizeDirection.Columns : GridResizeDirection.Rows,
        HorizontalAlignment = sideBySide ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        VerticalAlignment = sideBySide ? VerticalAlignment.Stretch : VerticalAlignment.Center,
        // Its own track is Auto, so it needs a size of its own: without one it
        // measures to nothing and cannot be grabbed.
        Width = sideBySide ? 6 : double.NaN,
        Height = sideBySide ? double.NaN : 6,
    };

    /// <summary>Says which panes the assistant has hold of, and which it has let go.</summary>
    private void Mark()
    {
        var driving = Driving ?? [];
        foreach (var (id, title) in _titles)
        {
            var held = driving.Contains(id);
            title.Strip.Classes.Set("driven", held);
            title.Mark.IsVisible = held;
        }
    }

    /// <summary>
    /// Puts a pane's frame on screen and says what it is.
    ///
    /// The focused pane is outlined. With one pane that says little; with four
    /// in a grid it is the only way to know where a keystroke goes.
    /// </summary>
    private Border Dress(PaneSlot slot)
    {
        var frame = Frame(slot);
        frame.IsVisible = true;
        frame.Classes.Set("active", slot.IsActive);
        frame.Classes.Set("tiled", IsTiled);

        if (_titles.TryGetValue(slot.Id, out var title))
        {
            // Named only when tiled: the tab strip says it the rest of the
            // time, and two labels for one pane is one too many.
            title.Strip.IsVisible = IsTiled && slot.Title.Length > 0;
            title.Name.Text = slot.Title;
            title.Name.Classes.Set("muted", !slot.IsActive);
        }

        return frame;
    }

    /// <summary>
    /// This pane's frame, made once and kept while the pane is open.
    ///
    /// The name above it is built with it rather than added when the window is
    /// tiled, and hidden the rest of the time: rebuilding the frame's contents
    /// would take the pane out of the visual tree, which is the one thing this
    /// class exists to avoid.
    /// </summary>
    private Border Frame(PaneSlot slot)
    {
        if (_frames.TryGetValue(slot.Id, out var existing))
            return existing;

        var name = new TextBlock
        {
            Text = slot.Title,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var mark = new TextBlock
        {
            Text = "\u25cf assistant",
            FontSize = 11,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Application.Current?.FindResource("AccentBrush") as IBrush,
        };

        var strip = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(mark, 1);
        strip.Children.Add(name);
        strip.Children.Add(mark);

        var header = new Border
        {
            Classes = { "tilename" },
            IsVisible = false,
            Child = strip,
        };

        var contents = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(new GridLength(1, GridUnitType.Star)) } };
        Grid.SetRow(header, 0);
        Grid.SetRow(slot.View, 1);
        contents.Children.Add(header);
        contents.Children.Add(slot.View);

        var frame = new Border { Classes = { "pane" }, Child = contents };
        _titles[slot.Id] = (header, name, mark);

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
