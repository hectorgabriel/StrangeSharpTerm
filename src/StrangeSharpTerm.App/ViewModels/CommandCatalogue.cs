using System.Windows.Input;
using Avalonia.Input;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>Which menu a command belongs under, in the order the menus appear.</summary>
public enum CommandGroup
{
    File,
    Edit,
    Session,
    View,
}

/// <summary>
/// One thing the app can be asked to do.
/// </summary>
/// <param name="Title">As the menu and the palette both say it.</param>
/// <param name="Gesture">Null for a command with no shortcut of its own.</param>
public sealed record AppCommand(
    string Id,
    string Title,
    CommandGroup Group,
    ICommand Command,
    object? Parameter = null,
    KeyGesture? Gesture = null)
{
    public bool CanRun => Command.CanExecute(Parameter);

    public void Run()
    {
        if (CanRun)
            Command.Execute(Parameter);
    }

    /// <summary>The shortcut as a menu shows it, or empty when there is none.</summary>
    public string Shortcut => Gesture?.ToString() ?? "";
}

/// <summary>
/// Everything the app can be asked to do, in one list.
///
/// The menu bar, the command palette and the window's key bindings are three
/// views of this. The Swift app declared its shortcuts in three places — a
/// <c>.commands</c> block, the palette, and <c>.keyboardShortcut</c> on views —
/// and the way that goes wrong is quiet: a menu item whose shortcut was changed
/// somewhere else, or a palette that offers something the menu does not.
///
/// Modifiers come from the platform rather than from a constant. ⌘ is Meta on
/// macOS and Control on Windows, which Avalonia already knows; see
/// docs/adr/0005.
/// </summary>
public static class CommandCatalogue
{
    /// <summary>
    /// Builds the list for a window's view model.
    /// </summary>
    /// <param name="command">
    /// The platform's own "command" modifier: ⌘ on macOS, Ctrl on Windows.
    /// Passed in rather than read from a static, so a test can ask for either.
    /// </param>
    public static IReadOnlyList<AppCommand> For(ShellViewModel shell, KeyModifiers command)
    {
        List<AppCommand> commands =
        [
            new("host.new", "New Host…", CommandGroup.File, shell.NewHostCommand,
                Gesture: new KeyGesture(Key.N, command)),
            new("folder.new", "New Folder…", CommandGroup.File, shell.NewFolderCommand,
                Gesture: new KeyGesture(Key.N, command | KeyModifiers.Shift)),
            // ⌘, — the one shortcut here that is not a port. Every macOS app
            // opens its settings with it, the Swift app got it for free from
            // SwiftUI's Settings scene, and Ctrl+, is the same habit on Windows.
            new("settings", "Settings…", CommandGroup.File, shell.OpenSettingsCommand,
                Gesture: new KeyGesture(Key.OemComma, command)),

            new("host.edit", "Edit Host…", CommandGroup.Edit, shell.EditSelectedCommand,
                Gesture: new KeyGesture(Key.E, command)),
            new("host.delete", "Delete Host…", CommandGroup.Edit, shell.DeleteSelectedCommand),
            // No shortcut: the Swift app's table has none for the library, and
            // it is opened rarely enough that the menu is the right place.
            new("credentials", "Credentials…", CommandGroup.Edit, shell.ManageCredentialsCommand),
            // Likewise no shortcut. The snippets themselves are in the palette,
            // which is how one is run; this is only the library that holds them.
            new("snippets", "Snippets…", CommandGroup.Edit, shell.ManageSnippetsCommand),

            new("session.open", "New Session", CommandGroup.Session, shell.ConnectSelectedCommand,
                Gesture: new KeyGesture(Key.T, command)),
            new("session.splitRight", "Split Side by Side", CommandGroup.Session, shell.SplitRightCommand,
                Gesture: new KeyGesture(Key.D, command)),
            new("session.splitDown", "Split Stacked", CommandGroup.Session, shell.SplitDownCommand,
                Gesture: new KeyGesture(Key.D, command | KeyModifiers.Shift)),
            new("session.closePane", "Close Pane", CommandGroup.Session, shell.ClosePaneCommand,
                Gesture: new KeyGesture(Key.W, command)),
            new("session.broadcast", "Broadcast to Every Pane", CommandGroup.Session, shell.ToggleBroadcastCommand,
                Gesture: new KeyGesture(Key.B, command | KeyModifiers.Alt)),
            new("session.files", "Browse Files", CommandGroup.Session, shell.BrowseFilesCommand,
                Gesture: new KeyGesture(Key.B, command | KeyModifiers.Shift)),
            // No shortcut: the Swift app's keyboard table has none for tunnels,
            // and inventing one risks colliding with a shortcut it does define.
            new("session.tunnels", "Tunnels", CommandGroup.Session, shell.OpenTunnelsCommand),

            // The two the Swift app's table has for the assistant, held back
            // since M4 because the thing they did did not exist yet. Alt is Alt
            // on both platforms, so ⌥⌘A is Ctrl+Alt+A on Windows.
            new("session.assistant", "Assistant", CommandGroup.Session, shell.OpenAssistantCommand,
                Gesture: new KeyGesture(Key.A, command | KeyModifiers.Alt)),
            new("session.orchestrator", "Ask Several Hosts…", CommandGroup.Session, shell.AskSeveralHostsCommand,
                Gesture: new KeyGesture(Key.A, command | KeyModifiers.Alt | KeyModifiers.Shift)),

            new("view.palette", "Command Palette…", CommandGroup.View, shell.OpenPaletteCommand,
                Gesture: new KeyGesture(Key.K, command)),
            // ⌘\ for the grid and ⌘B for the sidebar: neither is in the Swift
            // app's table, because neither existed there. ⌘B is what every
            // editor uses for a sidebar, and ⌥⌘B is already broadcasting.
            new("view.tiles", "Tile Every Session", CommandGroup.View, shell.ToggleTilesCommand,
                Gesture: new KeyGesture(Key.OemBackslash, command)),
            new("view.sidebar", "Show Hosts", CommandGroup.View, shell.ToggleSidebarCommand,
                Gesture: new KeyGesture(Key.B, command)),
        ];

        // ⌘1–⌘9, as the Swift app's ForEach(1...9) generated them. Nine items
        // rather than a rule, because a menu has to list them one by one.
        commands.AddRange(Enumerable.Range(1, 9).Select(number => new AppCommand(
            $"view.tab{number}",
            $"Tab {number}",
            CommandGroup.View,
            shell.FocusTabAtCommand,
            Parameter: number - 1,
            Gesture: new KeyGesture(Key.D0 + number, command))));

        return commands;
    }

    /// <summary>
    /// What the palette offers: everything with a title, best matches first.
    ///
    /// The numbered tab commands are left out — a palette listing "Tab 7" nine
    /// times is noise, and they exist for the keyboard rather than for reading.
    /// </summary>
    public static IReadOnlyList<AppCommand> ForPalette(IEnumerable<AppCommand> commands) =>
        [.. commands.Where(item => !item.Id.StartsWith("view.tab", StringComparison.Ordinal))];
}
