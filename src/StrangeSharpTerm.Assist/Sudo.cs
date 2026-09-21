namespace StrangeSharpTerm.Assist;

/// <summary>
/// When a host's password may be handed to <c>sudo</c>, and in what shape.
///
/// A command the assistant runs goes over an SSH exec channel: no terminal, and
/// running as the account the host was logged into. <c>sudo</c> there cannot
/// prompt, and the root shell somebody opened in the terminal pane is a
/// different channel altogether. So for a host where a person has said so, the
/// app gives <c>sudo</c> the host's own password on the channel's standard
/// input — never on the command line, where anybody on the server running
/// <c>ps</c> could read it.
///
/// <para>
/// The rule is deliberately narrow, and every limit in it is a case where the
/// password could end up read by something other than <c>sudo</c>:
/// </para>
/// <list type="bullet">
/// <item>One command, beginning with <c>sudo</c>. Two of them —
/// <c>sudo a &amp;&amp; sudo b</c> — would need the password twice, and a
/// short-circuit that skipped the second would leave the spare copy on the
/// input of whatever read next.</item>
/// <item>No substitution. <c>$(...)</c> can run anything, and anything can read
/// standard input.</item>
/// <item>No input redirection. <c>sudo cmd &lt; file</c> points
/// <c>sudo</c>'s input at the file instead, and the password is then read by
/// nothing — safe, but the command cannot work.</item>
/// <item>None of <c>sudo</c>'s own options that change whether or how it
/// asks: <c>-n</c>, <c>-A</c>, <c>-S</c>, <c>-k</c>, <c>-K</c>, <c>-v</c>,
/// <c>-l</c>, <c>-V</c>, <c>-e</c> and their long forms. With <c>-n</c>, for
/// one, it never reads a password at all, and the command after it would.</item>
/// </list>
/// <para>
/// What passes is run as <c>sudo -k -S -p '' ...</c>: <c>-S</c> to read the
/// password from standard input, <c>-p ''</c> so no prompt is printed into the
/// output, and <c>-k</c> so a cached credential cannot make it skip reading —
/// which would leave the password for the command to read instead.
/// </para>
/// <para>
/// The last case <c>-k</c> cannot close is a host where <c>sudo</c> needs no
/// password at all. That is what <see cref="Probe"/> is for: it is asked first,
/// without a password, and where it succeeds none is sent.
/// </para>
/// </summary>
public static class Sudo
{
    /// <summary>
    /// Asked first, with nothing on its input: does <c>sudo</c> need a password
    /// on this host at all? Where it does not, none is sent — because nothing
    /// would read it except the command that follows.
    /// </summary>
    public const string Probe = "sudo -n true";

    /// <summary>
    /// What a model is told when a command used sudo but did not qualify for the
    /// password, so it can fix the command rather than guess at why sudo said
    /// it needed a terminal.
    /// </summary>
    public const string NotGiven =
        "This host's password is given to sudo only for a single command that starts with sudo -- "
        + "not a chain, a pipeline or a substitution, and not with -n, -S, -A or -k. "
        + "Split it into one command per sudo and run them one at a time.";

    /// <summary>
    /// Whether any stage of the line runs sudo: sudo as the command, not as an
    /// argument. <c>echo sudo</c> does not; <c>apt-get update &amp;&amp; sudo
    /// reboot</c> does. Leading assignments are stepped over, as the policy
    /// steps over them -- <c>LANG=C sudo x</c> runs sudo.
    /// </summary>
    public static bool Mentions(string command) =>
        !string.IsNullOrWhiteSpace(command)
        && ShellWords.Stages(command).Any(stage =>
            stage.Where(token => token.Kind == ShellTokenKind.Word)
                .SkipWhile(token => !token.WasQuoted && token.Text.Contains('=') && !token.Text.StartsWith('='))
                .FirstOrDefault() is { Text.Length: > 0 } head
            && IsSudo(head));

    /// <summary>
    /// The command to run with the password on its input, or null where the
    /// password must not be given to this command.
    /// </summary>
    public static string? Prepared(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || ShellWords.HasSubstitution(command))
            return null;

        // One command and nothing else on the line -- a trailing & included,
        // which backgrounds sudo with its input taken from /dev/null.
        if (ShellWords.Separated(command))
            return null;

        var stages = ShellWords.Stages(command);
        if (stages.Count != 1)
            return null;

        var stage = stages[0];

        // Any input redirection would give sudo's input to something else.
        if (stage.Any(token => token.Kind == ShellTokenKind.Redirection && token.Text.Contains('<')))
            return null;

        var words = stage.Where(token => token.Kind == ShellTokenKind.Word).ToArray();
        if (words.Length == 0 || !IsSudo(words[0]))
            return null;

        // A second sudo would want the password again; the first is the only
        // one that gets it.
        if (words.Skip(1).Any(IsSudo))
            return null;

        if (!RunsACommand(words.AsSpan(1)))
            return null;

        // Inserted after the first word, as text, so the rest of the line is
        // run exactly as it was written and approved -- re-quoting it from
        // tokens would be a second opinion about someone else's quoting.
        var trimmed = command.TrimStart();
        var first = words[0].Text.Length;
        if (!trimmed.StartsWith(words[0].Text, StringComparison.Ordinal))
            return null;

        return $"{trimmed[..first]} -k -S -p ''{trimmed[first..]}";
    }

    /// <summary>
    /// Whether a word is <c>sudo</c> itself, bare or by path. Quoted it is not:
    /// <c>'sudo'</c> is an argument somebody meant as text.
    /// </summary>
    private static bool IsSudo(ShellToken word) =>
        !word.WasQuoted && (word.Text == "sudo" || word.Text.EndsWith("/sudo", StringComparison.Ordinal));

    /// <summary>
    /// Walks <c>sudo</c>'s own options and says whether they end in a command
    /// to run, with none of the ones that change how it asks on the way.
    /// </summary>
    private static bool RunsACommand(ReadOnlySpan<ShellToken> words)
    {
        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index].Text;

            if (word == "--")
                return index + 1 < words.Length;

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var name = word.Split('=', 2)[0];
                if (ForbiddenLong.Contains(name))
                    return false;
                if (TakesValueLong.Contains(name) && !word.Contains('='))
                    index++;
                continue;
            }

            if (word.StartsWith('-') && word.Length > 1)
            {
                // A cluster: -iu root, -Hn. Each letter is an option until one
                // that takes a value, which takes the rest of the word or the
                // next one.
                for (var at = 1; at < word.Length; at++)
                {
                    var letter = word[at];
                    if (ForbiddenShort.Contains(letter))
                        return false;
                    if (TakesValueShort.Contains(letter))
                    {
                        if (at == word.Length - 1)
                            index++;
                        break;
                    }
                }
                continue;
            }

            // The first word that is not an option is the command.
            return true;
        }

        // sudo with only options: nothing to run, so nothing to give it.
        return false;
    }

    /// <summary>
    /// Options after which <c>sudo</c> would not read the password this app
    /// sends, or would read it and run nothing — leaving it for whatever reads
    /// next, or sending it for no reason.
    /// </summary>
    private static readonly HashSet<char> ForbiddenShort = ['n', 'A', 'S', 'k', 'K', 'v', 'l', 'V', 'e'];

    private static readonly HashSet<string> ForbiddenLong = new(StringComparer.Ordinal)
    {
        "--non-interactive",
        "--askpass",
        "--stdin",
        "--reset-timestamp",
        "--remove-timestamp",
        "--validate",
        "--list",
        "--version",
        "--edit",
        "--help",
    };

    /// <summary>Options whose value is the next word, so it is not mistaken for the command.</summary>
    private static readonly HashSet<char> TakesValueShort = ['C', 'D', 'g', 'h', 'p', 'R', 'r', 'T', 't', 'U', 'u'];

    private static readonly HashSet<string> TakesValueLong = new(StringComparer.Ordinal)
    {
        "--close-from",
        "--chdir",
        "--group",
        "--host",
        "--prompt",
        "--chroot",
        "--role",
        "--command-timeout",
        "--type",
        "--other-user",
        "--user",
    };
}
