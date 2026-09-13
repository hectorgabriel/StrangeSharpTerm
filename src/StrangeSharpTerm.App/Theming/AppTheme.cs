using Avalonia;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Theming;

/// <summary>
/// Something on screen that carries terminal colours and can change them
/// without being rebuilt.
///
/// A terminal pane has an ssh session inside it. Recreating one to repaint it
/// would drop that session, which is why the Swift app deliberately kept its
/// redraw key away from terminals, and why the theme reaches a pane through
/// this rather than through the view being replaced.
/// </summary>
public interface IThemedPane
{
    void Apply(TerminalPalette palette);
}

/// <summary>
/// Which theme is in use, and everything that follows from changing it.
///
/// The Swift app read a static <c>Theme</c> from several hundred call sites.
/// Here the palette reaches the markup as resources
/// (<see cref="ThemeTokens.Apply"/>) and reaches open terminals through
/// <see cref="Changed"/>, so the only thing that needs an instance of this is
/// whoever offers the choice.
/// </summary>
public sealed class AppTheme
{
    private readonly string? _preferencesPath;

    /// <param name="preferencesPath">Where the choice is remembered. Null to not remember it, which is what a test wants.</param>
    public AppTheme(AppPalette? palette = null, string? preferencesPath = null)
    {
        Palette = palette ?? AppPalette.StrangeTermDark;
        _preferencesPath = preferencesPath;
    }

    public AppPalette Palette { get; private set; }

    /// <summary>Raised after the palette changes, never for a choice that changes nothing.</summary>
    public event EventHandler<AppPalette>? Changed;

    /// <summary>The theme remembered from last time, or the original one.</summary>
    public static AppTheme Load(string? preferencesPath = null)
    {
        var path = preferencesPath ?? Preferences.DefaultPath();
        return new AppTheme(AppPalette.ById(Preferences.Load(path).Theme), path);
    }

    /// <summary>
    /// Paints this theme into an application, now and whenever it changes.
    ///
    /// The subscription lived in <c>App.OnFrameworkInitializationCompleted</c>,
    /// which meant a window built any other way — a headless test, a preview —
    /// had a theme that never took effect. Wiring is the theme's own business.
    /// </summary>
    public void PaintInto(Application application)
    {
        ThemeTokens.Apply(application, Palette);
        Changed += (_, palette) => ThemeTokens.Apply(application, palette);
    }

    public void Use(AppPalette palette)
    {
        if (palette.Id == Palette.Id)
            return;

        Palette = palette;
        // Read back before writing, so a preference this version does not know
        // about survives a theme change.
        if (_preferencesPath is { } path)
            (Preferences.Load(path) with { Theme = palette.Id }).Save(path);
        Changed?.Invoke(this, palette);
    }

    /// <summary>
    /// The colours a host's terminal takes.
    ///
    /// A host — or a folder above it — may name a terminal theme of its own, and
    /// that wins: it is a setting someone set. Everything else follows the app
    /// theme, so choosing Dracula recolours the terminals that never asked for
    /// anything in particular.
    /// </summary>
    public TerminalPalette TerminalPaletteFor(InventoryTree tree, Connection connection)
    {
        string? named;
        try
        {
            named = connection.Settings.TerminalTheme
                ?? tree.Ancestors(connection.ParentId)
                    .Reverse()
                    .Select(folder => folder.Settings.TerminalTheme)
                    .FirstOrDefault(theme => theme is not null);
        }
        catch (InventoryException)
        {
            // A corrupted parent chain is the inventory's problem to report, not
            // a reason for a terminal to have no colours.
            named = null;
        }

        return named is null
            ? Palette.Terminal
            : TerminalPalette.BuiltIn.FirstOrDefault(palette => palette.Name == named) ?? Palette.Terminal;
    }
}
