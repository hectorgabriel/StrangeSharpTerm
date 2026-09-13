using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// The menu bar, and the shortcuts that work whether or not anyone opens it.
///
/// One <see cref="NativeMenu"/> serves both platforms: macOS shows it in the
/// system menu bar, Windows in a <c>NativeMenuBar</c> inside the window. Both
/// are generated from <see cref="CommandCatalogue"/>, so a menu item that does
/// nothing cannot exist — see docs/adr/0005.
/// </summary>
public static class AppMenu
{
    /// <summary>The platform's own command modifier: ⌘ on macOS, Ctrl on Windows.</summary>
    public static KeyModifiers CommandModifier(Visual window) =>
        window.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    public static NativeMenu Build(IReadOnlyList<AppCommand> commands)
    {
        var menu = new NativeMenu();
        foreach (var group in Enum.GetValues<CommandGroup>())
        {
            var items = commands.Where(command => command.Group == group).ToArray();
            if (items.Length == 0)
                continue;

            var submenu = new NativeMenu();
            foreach (var command in items)
                submenu.Add(new NativeMenuItem
                {
                    Header = command.Title,
                    Command = command.Command,
                    CommandParameter = command.Parameter,
                    Gesture = command.Gesture,
                });

            menu.Add(new NativeMenuItem(group.ToString()) { Menu = submenu });
        }
        return menu;
    }

    /// <summary>
    /// Binds every shortcut to the window.
    ///
    /// Separate from the menu on purpose: on Windows a <c>NativeMenuBar</c> only
    /// dispatches a gesture while it is on screen and, on either platform, a
    /// shortcut whose only home is a menu stops working the moment the menu is
    /// not there. The key bindings are the shortcuts; the menu is how they are
    /// found.
    /// </summary>
    public static void Bind(Window window, IReadOnlyList<AppCommand> commands)
    {
        window.KeyBindings.Clear();
        foreach (var command in commands.Where(command => command.Gesture is not null))
            window.KeyBindings.Add(new KeyBinding
            {
                Gesture = command.Gesture ?? throw new InvalidOperationException("filtered above"),
                Command = command.Command,
                // Null for most of them: only the tab commands carry one.
                CommandParameter = command.Parameter!,
            });
    }
}
