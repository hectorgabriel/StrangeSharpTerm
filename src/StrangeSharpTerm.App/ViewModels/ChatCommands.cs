using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// The few things you can type at an assistant that are not questions.
///
/// A line starting with a slash is handled here and never sent anywhere: it
/// costs no round trip, no tokens, and no waiting. That is the whole reason
/// they exist — "what tool servers are connected" and "forget this
/// conversation" are questions about the app, and asking a model about the app
/// it is running inside gets an answer that sounds right and is not checked
/// against anything.
///
/// Both panes take the same ones, because a person who learned them in one
/// would reasonably expect them in the other.
/// </summary>
public static class ChatCommands
{
    public const string Clear = "clear";

    public const string Mcp = "mcp";

    public const string Help = "help";

    public const string Export = "export";

    /// <summary>
    /// Whether this line is a command rather than a question.
    ///
    /// A slash and then a word. Anything else starting with a slash is a
    /// question that happens to start with a path — "/etc/nginx is missing,
    /// why?" — and sending that to a model is what the person meant.
    /// </summary>
    public static bool Looks(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('/') || trimmed.Length < 2)
            return false;

        var word = Name(trimmed);
        return word.Length > 0 && word.All(char.IsLetter);
    }

    /// <summary>The command's name, without the slash and in lower case.</summary>
    public static string Name(string text)
    {
        var trimmed = text.TrimStart().TrimStart('/');
        var cut = trimmed.IndexOf(' ');
        return (cut < 0 ? trimmed : trimmed[..cut]).ToLowerInvariant();
    }

    /// <summary>What <c>/help</c> answers with, and the only list of these there is.</summary>
    public static string Listing { get; } = string.Join(
        '\n',
        "Commands, handled here and never sent to the provider:",
        "",
        "  /clear   forget this conversation — what is on screen, and what the provider has been told",
        "  /mcp     the connected tool servers, and what each one offers",
        "  /export  copy this conversation to the clipboard as Markdown, secrets still taken out",
        "  /help    this",
        "",
        "Anything else is a question.");

    /// <summary>
    /// What <c>/export</c> says once the conversation is on the clipboard, or
    /// that there was nothing to put there.
    /// </summary>
    public static string Exported(IReadOnlyList<TranscriptEntry> entries)
    {
        var questions = entries.Count(entry => entry is TranscriptEntry.Question);
        var steps = entries.Count(entry => entry is TranscriptEntry.Step);
        return $"Copied this conversation to the clipboard as Markdown: {Count(questions, "question")}, {Count(steps, "command")}.";

        static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";
    }

    public const string NothingToExport = "There is nothing to export yet: ask something first.";

    /// <summary>What an unknown one is answered with, rather than being sent as a question.</summary>
    public static string Unknown(string name) =>
        $"There is no /{name}. Type /help for the ones there are, or ask without the slash to send it as a question.";
}
