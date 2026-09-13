using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The menu bar and the command palette, in a window.
///
/// Here rather than beside the other command tests because a
/// <c>NativeMenuItem</c> is an Avalonia object: building one without a platform
/// hangs the run, which is the same lesson a <c>GridSplitter</c> taught. The
/// shortcuts themselves are a list of data and are tested next door.
/// </summary>
[Collection("window")]
public class MenuAndPaletteTests
{
    private sealed class QuietChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    private static (ShellWindow Window, ShellViewModel Model, Connection Host) Open()
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var model = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            _ => new TerminalSession(new QuietChannel()),
            (_, _) => new Border());
        var window = new ShellWindow(model) { Width = 1100, Height = 700 };
        window.Show();
        Settle(window);
        return (window, model, host);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    [Fact]
    public void TheWindowDescribesItselfWhenItOpens()
    {
        Headless.Run(() =>
        {
            var (window, model, _) = Open();

            // The list exists, and its modifier came from the platform rather
            // than from a guess: on this one it is what the platform says.
            model.Commands.ShouldNotBeEmpty();
            var expected = AppMenu.CommandModifier(window);
            model.Commands.First(command => command.Id == "view.palette")
                .Gesture!.KeyModifiers.ShouldBe(expected);
        });
    }

    [Fact]
    public void EveryShortcutIsBoundToTheWindowItself()
    {
        Headless.Run(() =>
        {
            var (window, model, _) = Open();

            // Not only to the menu: a shortcut whose only home is a menu stops
            // working wherever that menu is not.
            var bound = window.KeyBindings.Select(binding => binding.Gesture.ToString()).ToArray();
            foreach (var command in model.Commands.Where(command => command.Gesture is not null))
                bound.ShouldContain(command.Gesture!.ToString());
        });
    }

    [Fact]
    public void TheMenuIsTheSameListGroupedIntoMenus()
    {
        Headless.Run(() =>
        {
            var (window, model, _) = Open();

            var menu = NativeMenu.GetMenu(window).ShouldNotBeNull();

            menu.Items.OfType<NativeMenuItem>().Select(item => item.Header)
                .ShouldBe(["File", "Edit", "Session", "View"]);

            var session = menu.Items.OfType<NativeMenuItem>().First(item => item.Header == "Session");
            session.Menu.ShouldNotBeNull()
                .Items.OfType<NativeMenuItem>()
                .Select(item => item.Header)
                .ShouldContain("Split Side by Side");

            // Every item carries the command it names: a menu entry that does
            // nothing cannot be written, because nobody writes the entries.
            foreach (var item in menu.Items.OfType<NativeMenuItem>()
                         .SelectMany(group => group.Menu!.Items)
                         .OfType<NativeMenuItem>())
                item.Command.ShouldNotBeNull();
        });
    }

    [Fact]
    public void TheMenuGreysOutWhatCannotBeDoneYet()
    {
        Headless.Run(() =>
        {
            var (window, model, host) = Open();
            var menu = NativeMenu.GetMenu(window)!;
            var edit = menu.Items.OfType<NativeMenuItem>()
                .SelectMany(group => group.Menu!.Items)
                .OfType<NativeMenuItem>()
                .First(item => item.Header == "Edit Host…");

            // Nothing selected: there is no host to edit.
            edit.IsEnabled.ShouldBeFalse();

            model.Inventory.Selection = host.Id;
            Settle(window);

            edit.IsEnabled.ShouldBeTrue();
        });
    }

    [Fact]
    public void TheShortcutOpensThePaletteAndEscapeClosesIt()
    {
        Headless.Run(() =>
        {
            var (window, model, _) = Open();

            window.KeyPress(Key.K, AppMenu.CommandModifier(window).ToRaw(), PhysicalKey.K, "k");
            Settle(window);

            model.Palette.IsOpen.ShouldBeTrue();
            // Drawn and holding the keyboard, not merely flagged: a palette you
            // have to click into before typing is not a palette.
            var field = In<TextBox>(window).First(box => box.Name == "PaletteQuery");
            field.IsEffectivelyVisible.ShouldBeTrue();
            field.IsFocused.ShouldBeTrue();

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
            Settle(window);

            model.Palette.IsOpen.ShouldBeFalse();
        });
    }

    [Fact]
    public void TypingNarrowsItAndEnterRunsWhatIsLeft()
    {
        Headless.Run(() =>
        {
            var (window, model, host) = Open();
            model.Inventory.Selection = host.Id;
            Settle(window);

            model.OpenPaletteCommand.Execute(null);
            Settle(window);

            // As typed into the field, not as assigned to the view model.
            window.KeyTextInput("new sess");
            Settle(window);

            model.Palette.Matches[0].Id.ShouldBe("session.open");

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
            // Opening a session is asynchronous; the command holds the task it
            // started, which is the honest thing to wait for.
            Headless.Finish(model.ConnectSelectedCommand.ExecutionTask ?? Task.CompletedTask);
            Settle(window);

            model.Palette.IsOpen.ShouldBeFalse();
            model.Tabs.ShouldHaveSingleItem();
        });
    }

    [Fact]
    public void TheArrowsMoveTheHighlight()
    {
        Headless.Run(() =>
        {
            var (window, model, _) = Open();
            model.OpenPaletteCommand.Execute(null);
            Settle(window);

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, "");
            Settle(window);
            model.Palette.SelectedIndex.ShouldBe(1);

            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, "");
            Settle(window);
            model.Palette.SelectedIndex.ShouldBe(0);
        });
    }

    [Fact]
    public void AShortcutDoesWhatTheMenuWouldHaveDone()
    {
        Headless.Run(() =>
        {
            var (window, model, host) = Open();
            model.Inventory.Selection = host.Id;
            Settle(window);

            // Cmd+T, the same as File ▸ New Session and the same as the palette.
            window.KeyPress(Key.T, AppMenu.CommandModifier(window).ToRaw(), PhysicalKey.T, "t");
            Headless.Finish(model.ConnectSelectedCommand.ExecutionTask ?? Task.CompletedTask);
            Settle(window);

            model.Tabs.ShouldHaveSingleItem();
        });
    }
}

/// <summary>Key modifiers as the headless input helpers want them.</summary>
internal static class ModifierExtensions
{
    public static RawInputModifiers ToRaw(this KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? RawInputModifiers.Control : RawInputModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Shift) ? RawInputModifiers.Shift : RawInputModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Alt) ? RawInputModifiers.Alt : RawInputModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.None);
}
