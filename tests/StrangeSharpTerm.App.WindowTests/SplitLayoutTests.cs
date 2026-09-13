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
    private static PaneSlot Slot(Control view, bool active = false) => new(NodeId.New(), view, active);

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

            var frame = first.Parent;
            frame.ShouldNotBeNull();
            InTheTree(first).ShouldBeTrue();

            view.Panes = [one with { IsActive = false }, Slot(new Border(), active: true)];
            Settle(window);

            // The same frame, still in the tree: not a new one that happens to
            // hold the same view.
            first.Parent.ShouldBeSameAs(frame);
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
            var frame = first.Parent;

            view.Axis = SplitAxis.Vertical;
            Settle(window);

            first.Parent.ShouldBeSameAs(frame);
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
            var leftFrame = (Border)left.Parent!;
            var rightFrame = (Border)right.Parent!;
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
