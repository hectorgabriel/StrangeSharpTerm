using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Mcp;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// A connected tool server being written.
///
/// The command and its arguments are one field, as a person writes them, and are
/// split on the way out: nobody types an argument array, and the thing they paste
/// from a README is a command line.
/// </summary>
public sealed partial class McpServerDraft : ObservableObject
{
    private readonly McpServerConfig _original;
    private readonly ISecretStore _store;

    private McpServerDraft(McpServerConfig original, ISecretStore store, bool isNew)
    {
        _original = original;
        _store = store;
        IsNew = isNew;

        Name = original.Name;
        IsHttp = original.Transport == McpTransport.Http;
        CommandLine = original.Where is { Length: > 0 } where && !IsHttp ? where : "";
        Url = original.Url;

        var credentials = McpTokens.Read(original, store);
        HasBearer = credentials.HasBearer;
        IsSignedIn = credentials.HasSignIn;
    }

    public static McpServerDraft New(ISecretStore store) =>
        new(new McpServerConfig { Name = "" }, store, isNew: true);

    public static McpServerDraft For(McpServerConfig server, ISecretStore store) =>
        new(server, store, isNew: false);

    public bool IsNew { get; }

    public string Title => IsNew ? "Add a tool server" : $"Edit {_original.Name}";

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>
    /// Which transport. A radio pair rather than a dropdown, because the two
    /// differ in what they expose rather than merely in mechanism.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHttp { get; set; }

    public bool IsLocal => !IsHttp;

    [ObservableProperty]
    public partial string CommandLine { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; }

    /// <summary>A token being pasted. Never read back out of the store to show.</summary>
    [ObservableProperty]
    public partial string NewBearer { get; set; } = "";

    [ObservableProperty]
    public partial bool HasBearer { get; private set; }

    [ObservableProperty]
    public partial bool IsSignedIn { get; private set; }

    /// <summary>What each transport means, said where the choice is made.</summary>
    public string TransportNote => IsHttp
        ? "Whatever a tool is given goes over the network to that address."
        : "Runs on this machine, as you, with your files.";

    /// <summary>
    /// Whether the command can be found, said before it is tried.
    ///
    /// An app launched from Finder inherits launchd's PATH rather than a shell's,
    /// so this is the most common way a local server fails to start.
    /// </summary>
    public string? CommandProblem
    {
        get
        {
            if (IsHttp)
                return null;
            var command = Words().FirstOrDefault() ?? "";
            return command.Length == 0 || CommandPath.Exists(command)
                ? null
                : $"{command} was not found. Looked on PATH and in {string.Join(", ", CommandPath.ExtraDirectories)}.";
        }
    }

    public string? Problem => Applied().Problem;

    public bool CanSave => Problem is null;

    partial void OnNameChanged(string value) => Refresh();

    partial void OnIsHttpChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(TransportNote));
        Refresh();
    }

    partial void OnCommandLineChanged(string value) => Refresh();

    partial void OnUrlChanged(string value) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CommandProblem));
    }

    /// <summary>The server as configured, keeping whatever was already granted.</summary>
    public McpServerConfig Applied()
    {
        var words = Words();
        return _original with
        {
            Name = Name.Trim(),
            Transport = IsHttp ? McpTransport.Http : McpTransport.Local,
            Command = IsHttp ? "" : words.FirstOrDefault() ?? "",
            Arguments = IsHttp ? [] : [.. words.Skip(1)],
            Url = IsHttp ? Url.Trim() : "",
        };
    }

    /// <summary>Writes whatever secret the sheet was given. Called after the server itself is saved.</summary>
    public void SaveSecret(McpServerConfig saved)
    {
        if (NewBearer.Trim() is not { Length: > 0 } bearer)
            return;

        var existing = McpTokens.Read(saved, _store);
        McpTokens.Write(saved, existing with { Bearer = bearer }, _store);
        NewBearer = "";
        HasBearer = true;
    }

    /// <summary>
    /// A command line as a shell would split it, near enough: quoted runs stay
    /// together, which is what a path with a space in it needs.
    /// </summary>
    internal IReadOnlyList<string> Words() => Split(CommandLine);

    internal static IReadOnlyList<string> Split(string line)
    {
        List<string> words = [];
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            words.Add(current.ToString());
        return words;
    }
}
