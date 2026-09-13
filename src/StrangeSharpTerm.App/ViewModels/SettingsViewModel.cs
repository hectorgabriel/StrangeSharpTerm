using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.Views;
using System.Collections.ObjectModel;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Mcp;
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
/// One connected server as the sheet draws it: what it is, where it is, how many
/// tools it has, and what it has been granted.
/// </summary>
public sealed record ServerRow(ServerStatus Status)
{
    public McpServerConfig Config => Status.Config;

    public string Name => Config.Name;

    public string Where => Config.Where;

    public string Summary => Status.Summary;

    public bool IsConnected => Status.IsConnected;

    /// <summary>Why it will not connect. Null when it is fine, and null when nothing has tried.</summary>
    public string? Failure => Status.Failure;

    public bool HasFailed => Status.Failure is not null;

    /// <summary>Shown rather than hidden: Settings says what has been granted and lets you take it back.</summary>
    public string? Granted => Status.Granted;

    public bool HasGrants => Granted is not null;

    /// <summary>Only a hosted server has anything to sign in to.</summary>
    public bool CanSignIn => Config.Transport == McpTransport.Http;
}

/// <summary>
/// The settings sheet: appearance, the assistant, connected tools, and the two
/// libraries.
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
    /// <param name="tools">
    /// The connected servers, or null when this sheet is not about any — a
    /// fixture, or a window built before they were configured.
    /// </param>
    public SettingsViewModel(
        AppTheme theme,
        Func<Task> credentials,
        Func<Task> snippets,
        AssistSettings? assist = null,
        ISecretStore? keys = null,
        Action<AssistSettings>? save = null,
        McpHub? tools = null,
        IDialogService? dialogs = null,
        ISecretStore? toolKeys = null)
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

        _tools = tools;
        _dialogs = dialogs ?? new ScriptedDialogService();
        _toolKeys = toolKeys ?? new InMemorySecretStore();
        OfferInPanes = ToolSettings.OfferInPanes;
        OfferInRuns = ToolSettings.OfferInRuns;
        RefreshServers();

        if (_tools is { } hub)
            hub.Changed += (_, _) => RefreshServers();
    }

    private readonly McpHub? _tools;
    private readonly IDialogService _dialogs;
    private readonly ISecretStore _toolKeys;

    private McpSettings ToolSettings => _tools?.Settings ?? new McpSettings();

    public ObservableCollection<ServerRow> Servers { get; } = [];

    public bool HasServers => Servers.Count > 0;

    /// <summary>
    /// Offer tools in assistant panes. On: connecting a server is already the
    /// deliberate act, and every call still asks.
    /// </summary>
    [ObservableProperty]
    public partial bool OfferInPanes { get; set; }

    /// <summary>
    /// Offer tools in orchestrated runs. Off: a fan-out points the same tools at
    /// the same place from every host, so one instruction can become one write
    /// per host, and the approvals arrive as a queue.
    /// </summary>
    [ObservableProperty]
    public partial bool OfferInRuns { get; set; }

    public static string ToolsNote =>
        "A local server runs on this machine as you, with your files. An HTTP server receives whatever "
        + "its tools are given. Nothing a server says about its own tools lets one skip the approval "
        + "gate — only you can.";

    [ObservableProperty]
    public partial string? ToolFailure { get; private set; }

    partial void OnOfferInPanesChanged(bool value) => ApplyTools(ToolSettings with { OfferInPanes = value });

    partial void OnOfferInRunsChanged(bool value) => ApplyTools(ToolSettings with { OfferInRuns = value });

    [RelayCommand]
    public async Task AddServer()
    {
        var draft = McpServerDraft.New(_toolKeys);
        if (!await _dialogs.Edit(draft))
            return;

        var saved = draft.Applied();
        draft.SaveSecret(saved);
        ApplyTools(ToolSettings.Upsert(saved));
        await Reconnect();
    }

    [RelayCommand]
    public async Task EditServer(ServerRow? row)
    {
        if (row is null)
            return;

        var draft = McpServerDraft.For(row.Config, _toolKeys);
        if (!await _dialogs.Edit(draft))
            return;

        var saved = draft.Applied();
        draft.SaveSecret(saved);
        ApplyTools(ToolSettings.Upsert(saved));
        await Reconnect();
    }

    /// <summary>Removes a server, and its grants and its tokens with it.</summary>
    [RelayCommand]
    public async Task RemoveServer(ServerRow? row)
    {
        if (row is null)
            return;
        if (!await _dialogs.Confirm(
            $"Remove {row.Name}?",
            "Its tools stop being offered, and anything you allowed it to run without asking is forgotten.",
            "Remove"))
        {
            return;
        }

        McpTokens.Forget(row.Config, _toolKeys);
        ApplyTools(ToolSettings.Remove(row.Config));
        await Reconnect();
    }

    /// <summary>Takes back every standing pass this server has.</summary>
    [RelayCommand]
    public void RevokeGrants(ServerRow? row)
    {
        if (row is null)
            return;
        ApplyTools(ToolSettings.Upsert(row.Config with { AlwaysAllowed = [] }));
        RefreshServers();
    }

    /// <summary>
    /// The visible login, which is the only place a browser is ever opened.
    /// </summary>
    [RelayCommand]
    public async Task SignIn(ServerRow? row)
    {
        if (row is null)
            return;

        ToolFailure = null;
        try
        {
            await McpSignIn.SignIn(row.Config, _toolKeys);
            await Reconnect();
        }
        catch (Exception e)
        {
            ToolFailure = e is McpException ? e.Message : $"{row.Name} could not be signed in to. ({e.Message})";
        }
    }

    private async Task Reconnect()
    {
        if (_tools is not { } hub)
            return;

        ToolFailure = null;
        try
        {
            await hub.Connect();
        }
        catch (Exception e)
        {
            ToolFailure = e.Message;
        }
        RefreshServers();
    }

    private void ApplyTools(McpSettings settings)
    {
        _tools?.Use(settings);
        RefreshServers();
    }

    private void RefreshServers()
    {
        Servers.Clear();
        // A configured server with no status yet is still a row: it was
        // configured, and a sheet that hid it until it connected would look like
        // it had lost it.
        var statuses = _tools?.Statuses ?? [];
        foreach (var server in ToolSettings.Servers)
        {
            // No failure for one nothing has tried: Summary already says it is
            // not connected, and a red line under a server that has simply not
            // been attempted reads as a server that is broken.
            var status = statuses.FirstOrDefault(known => known.Config.Id == server.Id)
                ?? new ServerStatus(server, 0, Failure: null);
            Servers.Add(new ServerRow(status with { Config = server }));
        }
        OnPropertyChanged(nameof(HasServers));
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
