using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Theming;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// A theme as the sheet offers it: named, described, and shown rather than
/// listed.
///
/// The swatch is four of its own colours in the order they stack up on screen —
/// rail, sidebar, background, accent — which is the Swift sheet's card, and the
/// only honest way to pick a theme is to see it.
/// </summary>
public sealed record ThemeChoice(AppPalette Palette, bool IsChosen)
{
    public string Name => Palette.Name;

    public string Description => Palette.Description;

    public IBrush Rail => Brush(Palette.Rail);

    public IBrush Sidebar => Brush(Palette.Sidebar);

    public IBrush Background => Brush(Palette.Background);

    public IBrush Accent => Brush(Palette.Accent);

    /// <summary>
    /// The card's outline: its accent when it is the chosen one, its own
    /// hairline otherwise. Drawn from the theme the card is <em>about</em>, so a
    /// card is a sample of itself rather than of whatever is in use.
    /// </summary>
    public IBrush Outline => Brush(IsChosen ? Palette.Accent : Palette.Border);

    private static IBrush Brush(uint colour) => new SolidColorBrush(ThemeTokens.ToColor(colour));
}

/// <summary>
/// The settings sheet.
///
/// The Swift app's had three sections: appearance, the assistant, and connected
/// tools. Only the first exists yet — the other two arrive with M6 and M7 — so
/// this holds appearance and the two libraries, which until now could only be
/// reached from the menu or by knowing their names in the palette.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppTheme _theme;
    private readonly Func<Task> _credentials;
    private readonly Func<Task> _snippets;

    public SettingsViewModel(AppTheme theme, Func<Task> credentials, Func<Task> snippets)
    {
        _theme = theme;
        _credentials = credentials;
        _snippets = snippets;
        Themes = Choices();
    }

    [ObservableProperty]
    public partial IReadOnlyList<ThemeChoice> Themes { get; private set; }

    /// <summary>
    /// Chooses a theme, which takes effect at once rather than on Done.
    ///
    /// There is nothing to confirm: the window in front of the user repaints,
    /// and that is both the preview and the answer. Open terminals keep their
    /// session and change colour in place — see <see cref="IThemedPane"/>.
    /// </summary>
    [RelayCommand]
    public void Choose(ThemeChoice? choice)
    {
        if (choice is null)
            return;

        _theme.Use(choice.Palette);
        Themes = Choices();
    }

    /// <summary>What choosing one does, said where the choice is made.</summary>
    public static string ThemeNote => "Open terminals keep their session; their colours change with the theme.";

    [RelayCommand]
    public async Task OpenCredentials() => await _credentials();

    [RelayCommand]
    public async Task OpenSnippets() => await _snippets();

    private IReadOnlyList<ThemeChoice> Choices() =>
        [.. AppPalette.BuiltIn.Select(palette => new ThemeChoice(palette, palette.Id == _theme.Palette.Id))];
}
