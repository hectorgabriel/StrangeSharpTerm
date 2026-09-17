using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// What a split does to the visual tree.
///
/// These are the assertions the split bug needed and the view-model tests could
/// not make: a pane that is moved must not leave the tree, because a terminal
/// control that leaves it tears its connection down — two panes then read one
/// stream and each drew half of it.
/// </summary>
[Collection("window")]
public class SplitLayoutTests
{
    private static PaneSlot Slot(Control view, bool active = false, string title = "") =>
        new(NodeId.New(), view, active, title);

    /// <summary>Still drawn somewhere, as opposed to merely still referenced.</summary>
    private static bool InTheTree(Visual control) =>
        control.GetSelfAndVisualAncestors().OfType<Window>().Any();

    /// <summary>Runs whatever the change queued: layout, styling, bindings.</summary>
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [Fact]
    public void APaneIsNeverTakenOutOfTheTreeWhenAnotherOpensBesideIt()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            var first = new Border();
            var one = Slot(first, active: true);
            view.Panes = [one];
            Settle(window);

            var frame = Frames.Of(first);
            InTheTree(first).ShouldBeTrue();

            view.Panes = [one with { IsActive = false }, Slot(new Border(), active: true)];
            Settle(window);

            // The same frame, still in the tree: not a new one that happens to
            // hold the same view.
            Frames.Of(first).ShouldBeSameAs(frame);
            InTheTree(first).ShouldBeTrue();
        });
    }

    [Fact]
    public void NorWhenTheAxisChangesUnderIt()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            var first = new Border();
            var one = Slot(first, active: true);
            var two = Slot(new Border());
            view.Panes = [one, two];
            Settle(window);
            var frame = Frames.Of(first);

            view.Axis = SplitAxis.Vertical;
            Settle(window);

            Frames.Of(first).ShouldBeSameAs(frame);
            InTheTree(first).ShouldBeTrue();
        });
    }

    [Fact]
    public void APaneThatClosesLetsGoOfItsView()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            var closing = new Border();
            var staying = Slot(new Border(), active: true);
            view.Panes = [staying, Slot(closing)];
            Settle(window);

            view.Panes = [staying];
            Settle(window);

            closing.Parent.ShouldBeNull();
            InTheTree(closing).ShouldBeFalse();
        });
    }

    [Fact]
    public void TwoPanesShareTheWidthAndOnlyOneIsOutlined()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            var left = new Border();
            var right = new Border();
            view.Panes = [Slot(left, active: true), Slot(right)];
            Settle(window);

            // Equal shares, give or take the divider between them.
            var leftFrame = Frames.Of(left);
            var rightFrame = Frames.Of(right);
            leftFrame.Bounds.Width.ShouldBe(rightFrame.Bounds.Width, tolerance: 1);
            leftFrame.Bounds.Height.ShouldBe(rightFrame.Bounds.Height, tolerance: 1);

            // Stacked, they share the height instead.
            view.Axis = SplitAxis.Vertical;
            Settle(window);
            leftFrame.Bounds.Width.ShouldBe(rightFrame.Bounds.Width, tolerance: 1);
            leftFrame.Bounds.Top.ShouldBeLessThan(rightFrame.Bounds.Top);

            // And the focused one is the one wearing the accent.
            leftFrame.Classes.ShouldContain("active");
            rightFrame.Classes.ShouldNotContain("active");
            ((ISolidColorBrush)leftFrame.BorderBrush!).Color
                .ShouldBe(ThemeTokens.ToColor(AppPalette.StrangeTermDark.Accent));
        });
    }

    [Fact]
    public void ClickingAPaneAsksForTheKeyboard()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            NodeId? asked = null;
            view.FocusCommand = new Command(id => asked = (NodeId?)id);

            // Painted, because an empty Border is not hit-testable and a real
            // pane — a terminal — always is.
            var left = Slot(new Border { Background = Brushes.Black }, active: true);
            var right = Slot(new Border { Background = Brushes.Black });
            view.Panes = [left, right];
            Settle(window);

            // A real click, on the right-hand half of the window.
            window.MouseDown(new Point(600, 300), MouseButton.Left);
            window.MouseUp(new Point(600, 300), MouseButton.Left);

            asked.ShouldBe(right.Id);
        });
    }

    /// <summary>
    /// Four sessions are two rows of two, which is the whole request: a window
    /// that shows everything it is connected to at once.
    /// </summary>
    [Fact]
    public void FourPanesTileIntoTwoRowsOfTwo()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView { IsTiled = true };
            var window = new Window { Content = view, Width = 1000, Height = 700 };
            window.Show();

            var panes = Enumerable.Range(0, 4).Select(_ => new Border()).ToArray();
            view.Panes = [.. panes.Select(pane => Slot(pane))];
            Settle(window);

            var frames = panes.Select(Frames.Of).ToArray();

            // Two across and two down, each the same size as the others.
            frames[0].Bounds.Width.ShouldBe(frames[1].Bounds.Width, tolerance: 1);
            frames[0].Bounds.Height.ShouldBe(frames[2].Bounds.Height, tolerance: 1);
            Left(frames[1], window).ShouldBeGreaterThan(Left(frames[0], window));
            Left(frames[2], window).ShouldBe(Left(frames[0], window), tolerance: 1);
            Top(frames[2], window).ShouldBeGreaterThan(Top(frames[0], window));
            Top(frames[1], window).ShouldBe(Top(frames[0], window), tolerance: 1);
        });
    }

    /// <summary>
    /// A count that does not divide leaves no hole: the last tile takes what the
    /// row has left, because an empty cell reads as a pane that failed to draw.
    /// </summary>
    [Fact]
    public void ThreePanesLeaveNoEmptyCell()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView { IsTiled = true };
            var window = new Window { Content = view, Width = 1000, Height = 700 };
            window.Show();

            var panes = Enumerable.Range(0, 3).Select(_ => new Border()).ToArray();
            view.Panes = [.. panes.Select(pane => Slot(pane))];
            Settle(window);

            var frames = panes.Select(Frames.Of).ToArray();

            // Two above, and the third spanning the width beneath them.
            Top(frames[2], window).ShouldBeGreaterThan(Top(frames[0], window));
            frames[2].Bounds.Width.ShouldBeGreaterThan(frames[0].Bounds.Width);
        });
    }

    /// <summary>
    /// A tile too narrow to read is not a tile. In a window with room for one
    /// column, four sessions are four rows rather than four slivers.
    /// </summary>
    [Fact]
    public void ANarrowWindowGivesUpColumnsRatherThanLegibility()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView { IsTiled = true };
            var window = new Window { Content = view, Width = 300, Height = 900 };
            window.Show();

            var panes = Enumerable.Range(0, 4).Select(_ => new Border()).ToArray();
            view.Panes = [.. panes.Select(pane => Slot(pane))];
            Settle(window);

            var frames = panes.Select(Frames.Of).ToArray();

            foreach (var frame in frames)
                Left(frame, window).ShouldBe(Left(frames[0], window), tolerance: 1);
            Top(frames[3], window).ShouldBeGreaterThan(Top(frames[0], window));
        });
    }

    /// <summary>
    /// Tiling moves panes between cells; it must never take one out of the tree.
    /// The same rule the split was built on, and the same consequence if it is
    /// broken: a terminal that leaves the tree loses its connection.
    /// </summary>
    [Fact]
    public void TilingDoesNotDetachAnything()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView();
            var window = new Window { Content = view, Width = 1000, Height = 700 };
            window.Show();

            var panes = Enumerable.Range(0, 4).Select(_ => new Border()).ToArray();
            view.Panes = [.. panes.Select(pane => Slot(pane))];
            Settle(window);
            var frames = panes.Select(Frames.Of).ToArray();

            view.IsTiled = true;
            Settle(window);

            for (var index = 0; index < panes.Length; index++)
            {
                Frames.Of(panes[index]).ShouldBeSameAs(frames[index]);
                InTheTree(panes[index]).ShouldBeTrue();
            }

            // And back again, with the spans it was given along the way undone.
            view.IsTiled = false;
            Settle(window);

            for (var index = 0; index < panes.Length; index++)
            {
                Frames.Of(panes[index]).ShouldBeSameAs(frames[index]);
                InTheTree(panes[index]).ShouldBeTrue();
            }

            Frames.Of(panes[0]).Bounds.Width.ShouldBe(Frames.Of(panes[1]).Bounds.Width, tolerance: 1);
        });
    }

    /// <summary>
    /// A tile says when the assistant is working in it, and stops saying so
    /// when it lets go.
    ///
    /// In a window showing eight servers, which one is being worked on is
    /// otherwise something you infer from output appearing -- and a host that
    /// is being changed is exactly the one worth being able to point at.
    /// </summary>
    [Fact]
    public void ATileSaysWhenTheAssistantIsWorkingInIt()
    {
        Headless.Run(() =>
        {
            var view = new PaneSplitView { IsTiled = true };
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();

            var panes = new[] { Slot(new Border(), title: "web-01"), Slot(new Border(), title: "web-02") };
            view.Panes = panes;
            Settle(window);

            Marks(window).ShouldAllBe(mark => !mark.IsVisible);

            view.Driving = [panes[0].Id];
            Settle(window);

            Frames.Of(panes[0].View).Child.ShouldNotBeNull();
            Marks(window).Count(mark => mark.IsVisible).ShouldBe(1);

            // And let go again the moment the command comes back.
            view.Driving = [];
            Settle(window);
            Marks(window).ShouldAllBe(mark => !mark.IsVisible);
        });
    }

    /// <summary>The words a tile can wear beside its name.</summary>
    private static TextBlock[] Marks(Visual root) =>
        [.. root.GetSelfAndVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.Text is not null && block.Text.Contains("assistant"))];

    private static double Left(Visual visual, Visual root) => visual.TranslatePoint(default, root)!.Value.X;

    private static double Top(Visual visual, Visual root) => visual.TranslatePoint(default, root)!.Value.Y;

    /// <summary>An ICommand that records what it was asked to do.</summary>
    private sealed class Command(Action<object?> run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run(parameter);
    }
}
