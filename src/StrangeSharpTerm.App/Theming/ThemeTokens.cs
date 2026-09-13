using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Theming;

/// <summary>
/// A palette as resources the markup can name.
///
/// Every colour the window paints comes from here as <c>{DynamicResource
/// SidebarBrush}</c> and the like, which is the whole reason the Swift app's
/// redraw hack has no counterpart: there, <c>Theme</c> was a static SwiftUI
/// could not observe, so <c>RootView</c> keyed each piece of chrome by theme id
/// to force it to be rebuilt. Avalonia re-evaluates a dynamic resource when the
/// dictionary changes, so switching themes is a dictionary write and nothing is
/// rebuilt — which matters most for a terminal pane, where being rebuilt would
/// mean losing the session inside it.
/// </summary>
public static class ThemeTokens
{
    /// <summary>
    /// How far a tint carries. Measured: a host badge in the Swift sidebar is
    /// its colour at this alpha over the surface beneath it
    /// (<c>build/theme/sample.py</c> solves them back out).
    /// </summary>
    public const double TintAlpha = 0.16;

    /// <summary>Every token, by name, without the <c>Brush</c> or <c>Color</c> suffix.</summary>
    public static IReadOnlyDictionary<string, uint> Of(AppPalette palette) => new Dictionary<string, uint>
    {
        ["Rail"] = palette.Rail,
        ["Sidebar"] = palette.Sidebar,
        ["Background"] = palette.Background,
        ["Surface"] = palette.Surface,
        ["Border"] = palette.Border,
        ["Selection"] = palette.Selection,
        ["Text"] = palette.Text,
        ["Muted"] = palette.Muted,
        ["Accent"] = palette.Accent,
        ["OnAccent"] = palette.OnAccent,
        ["Success"] = palette.Success,
        ["Warning"] = palette.Warning,
        ["Danger"] = palette.Danger,

        // A banner or a badge: the colour that means something, at the strength
        // it can sit behind text without competing with it.
        ["AccentTint"] = AppPalette.Blend(palette.Accent, palette.Background, TintAlpha),
        ["SuccessTint"] = AppPalette.Blend(palette.Success, palette.Background, TintAlpha),
        ["WarningTint"] = AppPalette.Blend(palette.Warning, palette.Background, TintAlpha),
        ["DangerTint"] = AppPalette.Blend(palette.Danger, palette.Background, TintAlpha),
    };

    /// <summary>
    /// Writes the palette into a resource dictionary, replacing whatever theme
    /// was there. Both a <c>Color</c> and a <c>Brush</c> for each token: markup
    /// wants brushes, and anything that computes with a colour wants the colour.
    /// </summary>
    public static void Apply(IResourceDictionary resources, AppPalette palette)
    {
        foreach (var (name, value) in Of(palette))
        {
            var colour = ToColor(value);
            resources[$"{name}Color"] = colour;
            resources[$"{name}Brush"] = new SolidColorBrush(colour);
        }
    }

    /// <summary>
    /// The same, for a running application, plus the one colour Fluent keeps to
    /// itself: its accent, which paints a focus ring, a caret and a checked box.
    /// It is the only palette entry Fluent re-reads at runtime — the rest are
    /// read once at startup — which is why nothing else is set through it.
    /// </summary>
    public static void Apply(Application application, AppPalette palette)
    {
        Apply(application.Resources, palette);

        if (application.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } fluent)
            return;
        if (!fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var fluentPalette))
            fluent.Palettes[ThemeVariant.Dark] = fluentPalette = new ColorPaletteResources();
        fluentPalette.Accent = ToColor(palette.Accent);
    }

    public static Color ToColor(uint colour)
    {
        var (red, green, blue) = TerminalPalette.Rgb(colour);
        return Color.FromRgb(red, green, blue);
    }
}
