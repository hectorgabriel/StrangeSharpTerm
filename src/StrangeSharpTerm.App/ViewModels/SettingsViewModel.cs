using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Security;

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
/// A provider as the sheet offers it: named, and saying where its data goes.
///
/// Where the data goes is on the card rather than in a help page, because it is
/// part of the choice. Terminal output is going to that country.
/// </summary>
public sealed record ProviderChoice(AssistProvider Provider, bool IsChosen, bool HasKey)
{
    public string Name => Provider.Name;

    public string DataGoesTo => $"Data goes to {Provider.DataGoesTo}";

    /// <summary>Whether a key is configured. Absence is ordinary and is said plainly.</summary>
    public string KeyNote => HasKey ? "API key saved" : $"No API key — or set {Provider.KeyVariable}";
}

/// <summary>
/// The settings sheet: appearance, the assistant, and the two libraries.
///
/// The Swift app's had three sections; connected tools is the one still to come,
/// with M7. The assistant's section is where a choice about what leaves the
/// machine is made, which is why both context switches are here rather than in a
/// pane that happens to be open.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppTheme _theme;
    private readonly Func<Task> _credentials;
    private readonly Func<Task> _snippets;
    private readonly ISecretStore _keys;
    private readonly Action<AssistSettings> _save;

    /// <param name="keys">
    /// Where API keys live — a store of their own, never the one connection
    /// credentials use.
    /// </param>
    /// <param name="save">Called whenever anything in the assistant section changes.</param>
    public SettingsViewModel(
        AppTheme theme,
        Func<Task> credentials,
        Func<Task> snippets,
        AssistSettings? assist = null,
        ISecretStore? keys = null,
        Action<AssistSettings>? save = null)
    {
        _theme = theme;
        _credentials = credentials;
        _snippets = snippets;
        _keys = keys ?? new InMemorySecretStore();
        _save = save ?? (_ => { });
        Themes = Choices();

        Assistant = assist ?? new AssistSettings();
        Model = Assistant.Model ?? "";
        SendMetrics = Assistant.SendMetrics;
        SendTerminalTail = Assistant.SendTerminalTail;
        Providers = ProviderChoices();
    }

    /// <summary>How the assistant is configured. Read back by whoever opened the sheet.</summary>
    public AssistSettings Assistant { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ProviderChoice> Providers { get; private set; }

    /// <summary>
    /// The model, as free text.
    ///
    /// Model names change faster than this app ships, and a model released after
    /// a build should not need a new one. <see cref="KnownModels"/> is a menu of
    /// suggestions, not the set of valid answers.
    /// </summary>
    [ObservableProperty]
    public partial string Model { get; set; }

    public IReadOnlyList<string> KnownModels => Assistant.Backend.KnownModels;

    /// <summary>What the field falls back to when it is left empty.</summary>
    public string DefaultModel => Assistant.Backend.DefaultModel;

    /// <summary>An API key being typed. Never read back out of the store to show.</summary>
    [ObservableProperty]
    public partial string NewKey { get; set; } = "";

    [ObservableProperty]
    public partial bool SendMetrics { get; set; }

    [ObservableProperty]
    public partial bool SendTerminalTail { get; set; }

    /// <summary>What the two switches decide, said where they are.</summary>
    public static string ContextNote =>
        "Every question carries the name you gave the host and what uname reported. These two add the rest, "
        + "and the pane shows exactly what will be sent before you ask.";

    [RelayCommand]
    public void ChooseProvider(ProviderChoice? choice)
    {
        if (choice is null || choice.IsChosen)
            return;

        // A pane is a conversation, and a conversation whose respondent changes
        // halfway through is not one -- so this changes what the next pane uses,
        // and the ones already open keep answering where they were.
        Apply(Assistant with { Provider = choice.Provider.Id, Model = null });
        Model = "";
        OnPropertyChanged(nameof(KnownModels));
        OnPropertyChanged(nameof(DefaultModel));
    }

    /// <summary>
    /// Saves a key, under the chosen provider's own account.
    ///
    /// One account per provider, so configuring a second does not overwrite the
    /// first. The field is cleared afterwards: there is nothing to gain from
    /// leaving a key on screen.
    /// </summary>
    [RelayCommand]
    public void SaveKey()
    {
        var key = NewKey.Trim();
        if (key.Length == 0)
            return;

        try
        {
            _keys.SetSecret(Assistant.Backend.KeyAccount, key);
        }
        catch (SecretStoreException e)
        {
            KeyFailure = e.Message;
            return;
        }

        NewKey = "";
        KeyFailure = null;
        Providers = ProviderChoices();
    }

    [RelayCommand]
    public void ForgetKey()
    {
        try
        {
            _keys.RemoveSecret(Assistant.Backend.KeyAccount);
            KeyFailure = null;
        }
        catch (SecretStoreException e)
        {
            KeyFailure = e.Message;
        }
        Providers = ProviderChoices();
    }

    /// <summary>Why the last key operation failed, or null.</summary>
    [ObservableProperty]
    public partial string? KeyFailure { get; private set; }

    partial void OnModelChanged(string value) =>
        Apply(Assistant with { Model = value.Trim().Length == 0 ? null : value.Trim() });

    partial void OnSendMetricsChanged(bool value) => Apply(Assistant with { SendMetrics = value });

    partial void OnSendTerminalTailChanged(bool value) => Apply(Assistant with { SendTerminalTail = value });

    private void Apply(AssistSettings settings)
    {
        Assistant = settings;
        _save(settings);
        OnPropertyChanged(nameof(Assistant));
    }

    private IReadOnlyList<ProviderChoice> ProviderChoices() =>
    [
        .. AssistProvider.All.Select(provider => new ProviderChoice(
            provider,
            provider.Id == Assistant.Provider,
            AssistKeys.Key(provider, _keys) is { Length: > 0 })),
    ];

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
