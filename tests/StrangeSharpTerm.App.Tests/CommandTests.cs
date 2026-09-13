using Avalonia.Controls;
using Avalonia.Input;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The one list the menu, the palette and the shortcuts are all views of, and
/// what happens when a command is asked for by name.
///
/// The menu built from it is in the window tests instead: a NativeMenuItem is an
/// Avalonia object and wants a platform, the same way a GridSplitter does.
/// </summary>
public class CommandTests
{
    private sealed class DeadChannel : ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    private static (ShellViewModel Shell, Connection Host) Arrange(KeyModifiers command = KeyModifiers.Meta)
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            _ => new TerminalSession(new DeadChannel()),
            (_, _) => new Border());
        shell.Describe(command);
        return (shell, host);
    }

    [Fact]
    public void EveryShortcutTheSwiftAppBoundThatHasSomethingToDoIsBound()
    {
        // The rest of its table waits for the milestone that builds the thing:
        // SFTP in M5, the assistant in M6, disconnect and its Windows gesture
        // with them. docs/adr/0005 lists them and says why.
        var (shell, _) = Arrange();
        var bound = shell.Commands.Where(command => command.Gesture is not null)
            .ToDictionary(command => command.Id, command => command.Gesture!.ToString());

        bound["view.palette"].ShouldBe("Cmd+K");
        bound["session.open"].ShouldBe("Cmd+T");
        bound["session.splitRight"].ShouldBe("Cmd+D");
        bound["session.splitDown"].ShouldBe("Shift+Cmd+D");
        bound["session.closePane"].ShouldBe("Cmd+W");
        bound["session.broadcast"].ShouldBe("Alt+Cmd+B");
        bound["host.new"].ShouldBe("Cmd+N");
        bound["folder.new"].ShouldBe("Shift+Cmd+N");
        bound["host.edit"].ShouldBe("Cmd+E");
        bound["view.tab1"].ShouldBe("Cmd+D1");
        bound["view.tab9"].ShouldBe("Cmd+D9");
    }

    [Fact]
    public void OnWindowsTheSameListSaysControl()
    {
        // Asked of the platform rather than written down twice: ⌘ is Meta on
        // macOS and Control on Windows, and one list serves both.
        var (shell, _) = Arrange(KeyModifiers.Control);
        var bound = shell.Commands.ToDictionary(command => command.Id, command => command.Gesture?.ToString());

        bound["view.palette"].ShouldBe("Ctrl+K");
        bound["session.splitDown"].ShouldBe("Ctrl+Shift+D");
        bound["session.broadcast"].ShouldBe("Ctrl+Alt+B");
        bound["view.tab3"].ShouldBe("Ctrl+D3");
    }

    [Fact]
    public void NoTwoCommandsWantTheSameKeys()
    {
        foreach (var modifier in new[] { KeyModifiers.Meta, KeyModifiers.Control })
        {
            var (shell, _) = Arrange(modifier);
            var gestures = shell.Commands.Where(command => command.Gesture is not null)
                .Select(command => command.Gesture!.ToString())
                .ToArray();

            gestures.Distinct().Count().ShouldBe(gestures.Length, $"a shortcut is bound twice with {modifier}");
        }
    }

    [Fact]
    public void EveryCommandHasSomethingToRun()
    {
        var (shell, _) = Arrange();

        shell.Commands.ShouldAllBe(command => command.Command != null);
        shell.Commands.Select(command => command.Id).Distinct().Count().ShouldBe(shell.Commands.Count);
        shell.Commands.ShouldAllBe(command => command.Title.Length > 0);
    }

    [Fact]
    public void ThePaletteOffersWhatCanBeDoneNow()
    {
        // Not a menu: a greyed-out row answers a question nobody asked. With no
        // session open, closing a pane is not on the list.
        var (shell, _) = Arrange();
        shell.Palette.Open();

        shell.Palette.Matches.Select(command => command.Id).ShouldNotContain("session.closePane");
        shell.Palette.Matches.Select(command => command.Id).ShouldContain("host.new");
    }

    [Fact]
    public async Task WhatCanBeDoneChangesWithWhatIsOpen()
    {
        var (shell, host) = Arrange();
        shell.Inventory.Selection = host.Id;
        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        shell.Palette.Open();

        shell.Palette.Matches.Select(command => command.Id).ShouldContain("session.closePane");
        // Broadcasting still needs a second pane.
        shell.Palette.Matches.Select(command => command.Id).ShouldNotContain("session.broadcast");
    }

    [Fact]
    public void TypingFewLettersFindsTheCommand()
    {
        var (shell, _) = Arrange();
        shell.Palette.Open();

        shell.Palette.Query = "nh";

        // "New Host" by its initials, which is the reason FuzzyMatch was ported.
        shell.Palette.Matches[0].Id.ShouldBe("host.new");
        shell.Palette.SelectedIndex.ShouldBe(0);
    }

    [Fact]
    public void TheHighlightGoesBackToTheTopAsTheListNarrows()
    {
        // Otherwise Enter runs whatever happens to be under an old highlight.
        var (shell, _) = Arrange();
        shell.Palette.Open();
        shell.Palette.Move(2);
        shell.Palette.SelectedIndex.ShouldBe(2);

        shell.Palette.Query = "new";

        shell.Palette.SelectedIndex.ShouldBe(0);
    }

    [Fact]
    public void TheSelectionStopsAtEachEndRatherThanWrapping()
    {
        var (shell, _) = Arrange();
        shell.Palette.Open();

        shell.Palette.Move(-1);
        shell.Palette.SelectedIndex.ShouldBe(0);

        shell.Palette.Move(100);
        shell.Palette.SelectedIndex.ShouldBe(shell.Palette.Matches.Count - 1);
    }

    [Fact]
    public void RunningFromThePaletteClosesItAndDoesTheThing()
    {
        var (shell, host) = Arrange();
        shell.Inventory.Selection = host.Id;
        shell.Palette.Open();
        shell.Palette.Query = "edit host";

        shell.Palette.RunSelected();

        shell.Palette.IsOpen.ShouldBeFalse();
        // The editor was asked for: the scripted dialog service recorded a draft.
        shell.Palette.Query.ShouldBe("edit host");
    }

    [Fact]
    public void ItOpensEmptyEveryTime()
    {
        // The last question is not what the next one starts from.
        var (shell, _) = Arrange();
        shell.Palette.Open();
        shell.Palette.Query = "split";
        shell.Palette.Close();

        shell.Palette.Open();

        shell.Palette.Query.ShouldBe("");
        shell.Palette.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void NothingMatchingSelectsNothing()
    {
        var (shell, _) = Arrange();
        shell.Palette.Open();

        shell.Palette.Query = "zzzz";

        shell.Palette.Matches.ShouldBeEmpty();
        shell.Palette.SelectedIndex.ShouldBe(-1);
        shell.Palette.Selected.ShouldBeNull();

        // And Enter on nothing closes rather than throwing.
        shell.Palette.RunSelected();
        shell.Palette.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task TheTabCommandsAreForTheKeyboardRatherThanForReading()
    {
        var (shell, host) = Arrange();
        shell.Inventory.Selection = host.Id;
        await shell.ConnectSelectedCommand.ExecuteAsync(null);
        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        shell.Palette.Open();
        shell.Palette.Matches.Select(command => command.Id).ShouldNotContain("view.tab1");

        // They still work: the second tab is the one Cmd+2 goes to.
        shell.Commands.First(command => command.Id == "view.tab1").Run();
        shell.Tabs[0].IsActive.ShouldBeTrue();
    }
}
