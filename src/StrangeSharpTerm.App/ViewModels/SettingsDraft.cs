using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// A setting that may simply not be answered here.
///
/// <see cref="ConnectionSettings"/> holds <c>bool?</c>, where null means "ask my
/// parent". A checkbox cannot say that — an unchecked box is an answer — so the
/// editor offers three states and this is the third one, named.
/// </summary>
public enum Inheritable
{
    Inherited,
    Yes,
    No,
}

/// <summary>One option in a picker: what it says, and what it means.</summary>
/// <param name="Value">
/// Untyped because the pickers carry three different kinds of value —
/// a tri-state, a host key policy, a theme name — and null is a real choice in
/// all three. The binding compares it against the draft's own property.
/// </param>
public sealed record Choice(string Label, object? Value);

/// <summary>
/// The editable form of <see cref="ConnectionSettings"/>: strings, because a
/// half-typed port is a string and not an <c>int?</c>.
///
/// Folders and hosts carry the same partial settings, so they edit them through
/// the same draft. Blank means inherited throughout, which is the one rule the
/// whole form runs on.
///
/// What is <em>not</em> here matters as much: environment variables, port
/// forwards, the credential, and the terminal font size are all real settings
/// this form does not show. <see cref="ApplyTo"/> folds onto the original record
/// rather than building a new one, so editing a host's port cannot silently drop
/// its port forwards.
/// </summary>
public sealed partial class SettingsDraft : ObservableObject
{
    /// <summary>Reads the settings a node already has into the form.</summary>
    /// <param name="credentials">
    /// The library, for the picker. Empty when a draft is built without an
    /// inventory behind it, which is what most tests want.
    /// </param>
    public SettingsDraft(ConnectionSettings? settings = null, IReadOnlyList<Credential>? credentials = null)
    {
        var source = settings ?? ConnectionSettings.Empty;
        _original = source;
        Username = source.Username ?? "";
        Port = Number(source.Port);
        ConnectTimeout = Number(source.ConnectTimeout);
        KeepAliveInterval = Number(source.KeepAliveInterval);
        Compression = Tristate(source.Compression);
        ForwardAgent = Tristate(source.ForwardAgent);
        HostKeyPolicyChoice = Policies.First(choice => Equals(choice.Value, source.HostKeyPolicy));
        KnownHostsFile = source.KnownHostsFile ?? "";
        // A theme this version has never heard of still round-trips: it joins the
        // list rather than being quietly reset to inherited.
        ThemeChoices = Themes.Any(choice => Equals(choice.Value, source.TerminalTheme))
            ? Themes
            : [.. Themes, new Choice(source.TerminalTheme!, source.TerminalTheme)];
        TerminalThemeChoice = ThemeChoices.First(choice => Equals(choice.Value, source.TerminalTheme));
        IdentityFiles = Lines(source.IdentityFiles);
        JumpHosts = string.Join(", ", source.JumpHosts ?? []);

        // Inherited, not "None": a credential set on a folder covers every host
        // beneath it, so a host that names none takes its folder's rather than
        // authenticating with nothing. And, as with the other pickers, the empty
        // answer needs an entry of its own — a null selection draws blank.
        List<Choice> library =
        [
            new Choice("Inherited", null),
            .. (credentials ?? []).Select(credential => new Choice(credential.Name, credential.Id)),
        ];

        // A credential this library does not have still round-trips rather than
        // being quietly cleared: the id is in the file, and a form that drops it
        // because it cannot name it is a form that loses data on Save. The same
        // rule as a terminal theme this version has never heard of.
        if (source.CredentialId is { } named && !library.Any(choice => Equals(choice.Value, named)))
            library.Add(new Choice("A credential that is no longer here", named));

        CredentialChoices = library;
        CredentialChoice = library.First(choice => Equals(choice.Value, source.CredentialId));
    }

    private readonly ConnectionSettings _original;

    [ObservableProperty]
    public partial string Username { get; set; }

    [ObservableProperty]
    public partial string Port { get; set; }

    [ObservableProperty]
    public partial string ConnectTimeout { get; set; }

    [ObservableProperty]
    public partial string KeepAliveInterval { get; set; }

    [ObservableProperty]
    public partial Inheritable Compression { get; set; }

    [ObservableProperty]
    public partial Inheritable ForwardAgent { get; set; }

    /// <summary>
    /// The chosen option rather than its value, because "Inherited" <em>is</em>
    /// null and a picker cannot select null — it reads as nothing chosen, and the
    /// field draws blank.
    /// </summary>
    [ObservableProperty]
    public partial Choice HostKeyPolicyChoice { get; set; }

    public HostKeyPolicy? HostKeyPolicy => HostKeyPolicyChoice.Value as HostKeyPolicy?;

    [ObservableProperty]
    public partial string KnownHostsFile { get; set; }

    /// <inheritdoc cref="HostKeyPolicyChoice"/>
    [ObservableProperty]
    public partial Choice CredentialChoice { get; set; }

    /// <summary>The shared credential this node uses, or null for none of them.</summary>
    public NodeId? CredentialId => CredentialChoice.Value as NodeId?;

    /// <summary>What the picker offers: the library, plus none.</summary>
    public IReadOnlyList<Choice> CredentialChoices { get; }

    /// <inheritdoc cref="HostKeyPolicyChoice"/>
    [ObservableProperty]
    public partial Choice TerminalThemeChoice { get; set; }

    /// <summary>By name, as the inventory records it. Null is inherited.</summary>
    public string? TerminalTheme => TerminalThemeChoice.Value as string;

    /// <summary>The built-in themes, plus whatever this node already names.</summary>
    public IReadOnlyList<Choice> ThemeChoices { get; }

    /// <summary>One path per line: paths contain spaces and commas do not separate them.</summary>
    [ObservableProperty]
    public partial string IdentityFiles { get; set; }

    /// <summary>The ProxyJump chain, nearest hop first, comma-separated as ssh writes it.</summary>
    [ObservableProperty]
    public partial string JumpHosts { get; set; }

    /// <summary>
    /// What a blank username or port would resolve to, for the field to show in
    /// grey. Set by whoever owns this draft, because only they know where in the
    /// tree the node sits — and it changes when the folder does.
    /// </summary>
    [ObservableProperty]
    public partial string UsernameWatermark { get; set; } = "";

    /// <inheritdoc cref="UsernameWatermark"/>
    [ObservableProperty]
    public partial string PortWatermark { get; set; } = "";

    /// <summary>Inherited, or an answer. The third state is the point of the list.</summary>
    public static IReadOnlyList<Choice> Tristates { get; } =
    [
        new("Inherited", Inheritable.Inherited),
        new("Enabled", Inheritable.Yes),
        new("Disabled", Inheritable.No),
    ];

    public static IReadOnlyList<Choice> Policies { get; } =
    [
        new("Inherited", null),
        new("Only keys already trusted", Model.HostKeyPolicy.Strict),
        new("Ask on first contact", Model.HostKeyPolicy.AcceptNew),
        new("Any key, without asking", Model.HostKeyPolicy.AcceptAny),
    ];

    /// <summary>By name, because that is what the inventory records.</summary>
    public static IReadOnlyList<Choice> Themes { get; } =
    [
        new("Inherited", null),
        .. TerminalPalette.BuiltIn.Select(palette => new Choice(palette.Name, palette.Name)),
    ];

    /// <summary>What is wrong with the form, in the order the fields appear.</summary>
    public IEnumerable<string> Problems()
    {
        if (Whole(Port) is null && Port.Trim().Length > 0)
            yield return "Port must be a whole number between 1 and 65535.";
        else if (Whole(Port) is { } port && port is < 1 or > 65535)
            yield return "Port must be between 1 and 65535.";

        if (Positive(ConnectTimeout) is false)
            yield return "Connect timeout must be a positive number of seconds.";

        if (Whole(KeepAliveInterval) is null && KeepAliveInterval.Trim().Length > 0)
            yield return "Keep-alive must be a whole number of seconds, or 0 to switch it off.";
        else if (Whole(KeepAliveInterval) is { } keepAlive && keepAlive < 0)
            yield return "Keep-alive cannot be negative.";
    }

    /// <summary>
    /// Folds the form onto the settings it was read from, leaving every field the
    /// form does not show exactly as it was.
    /// </summary>
    public ConnectionSettings ApplyTo(ConnectionSettings original) => original with
    {
        Username = Blank(Username),
        Port = Whole(Port),
        ConnectTimeout = Whole(ConnectTimeout),
        KeepAliveInterval = Whole(KeepAliveInterval),
        Compression = Bool(Compression),
        ForwardAgent = Bool(ForwardAgent),
        HostKeyPolicy = HostKeyPolicy,
        CredentialId = CredentialId,
        KnownHostsFile = Blank(KnownHostsFile),
        TerminalTheme = TerminalTheme,
        IdentityFiles = List(IdentityFiles, '\n'),
        JumpHosts = List(JumpHosts, ','),
    };

    /// <summary>The same, onto the settings the draft was opened with.</summary>
    public ConnectionSettings Applied() => ApplyTo(_original);

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static Inheritable Tristate(bool? value) =>
        value switch { true => Inheritable.Yes, false => Inheritable.No, null => Inheritable.Inherited };

    private static bool? Bool(Inheritable value) =>
        value switch { Inheritable.Yes => true, Inheritable.No => false, _ => null };

    private static string Lines(IReadOnlyList<string>? values) => string.Join(Environment.NewLine, values ?? []);

    private static string? Blank(string value) => value.Trim().Length == 0 ? null : value.Trim();

    private static int? Whole(string value) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool? Positive(string value) =>
        value.Trim().Length == 0 ? null : Whole(value) is > 0;

    /// <summary>
    /// A list, or null for "inherited". An empty list is not the same as null:
    /// it would mean "this host has no identity files", overriding the folder's.
    /// </summary>
    private static IReadOnlyList<string>? List(string text, char separator)
    {
        string[] items =
        [
            .. text.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim('\r')),
        ];
        return items.Length == 0 ? null : items;
    }
}
