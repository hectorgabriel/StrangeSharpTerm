using System.Text;

namespace StrangeSharpTerm.Assist;

internal enum ShellTokenKind
{
    Word,

    /// <summary><c>;</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>&amp;</c>, or a newline: the end of one stage.</summary>
    Separator,

    /// <summary><c>&gt;</c>, <c>&gt;&gt;</c>, <c>&lt;</c>, <c>2&gt;</c>, and the rest.</summary>
    Redirection,
}

internal readonly record struct ShellToken(string Text, ShellTokenKind Kind, bool WasQuoted);

/// <summary>
/// Just enough of a shell to judge a command by.
///
/// Not an interpreter and deliberately not one: it exists so that
/// <see cref="CommandPolicy"/> can see every stage of a pipeline, the real
/// command at the head of each, and whether anything is being written to. What
/// it cannot make sense of, the policy refuses.
/// </summary>
internal static class ShellWords
{
    /// <summary>
    /// Whether the text contains a substitution of any kind.
    ///
    /// Asked before anything is parsed, and answered on the raw text -- a
    /// quoted <c>$(...)</c> is inert, but distinguishing the inert ones is
    /// exactly the kind of cleverness that eventually gets one wrong. A
    /// read-only command wrongly stopped costs a click.
    /// </summary>
    internal static bool HasSubstitution(string command) =>
        command.Contains("$(", StringComparison.Ordinal)
        || command.Contains('`', StringComparison.Ordinal)
        || command.Contains("<(", StringComparison.Ordinal)
        || command.Contains(">(", StringComparison.Ordinal);

    /// <summary>
    /// Splits a command line into its stages, each a list of tokens.
    ///
    /// Every stage is judged, not only the first: <c>ps aux | tee /tmp/x</c>
    /// writes a file, and a check that read only the head of the line would say
    /// it was <c>ps</c>.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<ShellToken>> Stages(string command)
    {
        List<IReadOnlyList<ShellToken>> stages = [];
        List<ShellToken> current = [];

        foreach (var token in Scan(command))
        {
            if (token.Kind == ShellTokenKind.Separator)
            {
                if (current.Count > 0)
                    stages.Add(current);
                current = [];
                continue;
            }
            current.Add(token);
        }

        if (current.Count > 0)
            stages.Add(current);
        return stages;
    }

    /// <summary>
    /// Whether the line is more than one command: any <c>;</c>, <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>|</c>, <c>&amp;</c> or newline, trailing ones included.
    ///
    /// Its own question because <see cref="Stages"/> drops a separator with
    /// nothing after it, which is right for judging what a line runs and wrong
    /// for asking whether it is one command: <c>sudo whoami &amp;</c> is one
    /// stage and a background job.
    /// </summary>
    internal static bool Separated(string command) =>
        Scan(command).Any(token => token.Kind == ShellTokenKind.Separator);

    private static IEnumerable<ShellToken> Scan(string command)
    {
        var word = new StringBuilder();
        var quoted = false;
        var started = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            switch (c)
            {
                case '\'' or '"':
                    {
                        var closing = command.IndexOf(c, i + 1);
                        // An unterminated quote: take the rest as one word. The
                        // policy will not recognise it and will stop, which is
                        // the right end for a line nobody can parse.
                        var end = closing < 0 ? command.Length : closing;
                        word.Append(command, i + 1, end - i - 1);
                        (quoted, started) = (true, true);
                        i = end;
                        continue;
                    }

                case '\\' when i + 1 < command.Length:
                    word.Append(command[i + 1]);
                    started = true;
                    i++;
                    continue;

                case ' ' or '\t':
                    if (started)
                        yield return Finish();
                    continue;
            }

            if (Separator(command, i) is { } separator)
            {
                if (started)
                    yield return Finish();
                yield return new ShellToken(separator, ShellTokenKind.Separator, false);
                i += separator.Length - 1;
                continue;
            }

            if (Redirection(command, i) is { } redirection)
            {
                if (started)
                    yield return Finish();
                yield return new ShellToken(redirection, ShellTokenKind.Redirection, false);
                i += redirection.Length - 1;
                continue;
            }

            word.Append(c);
            started = true;
        }

        if (started)
            yield return Finish();

        ShellToken Finish()
        {
            var token = new ShellToken(word.ToString(), ShellTokenKind.Word, quoted);
            word.Clear();
            (quoted, started) = (false, false);
            return token;
        }
    }

    private static string? Separator(string command, int at) => command[at] switch
    {
        '\n' or ';' => command[at].ToString(),
        '&' => Ahead(command, at, '&') ? "&&" : "&",
        // A pipe, but not the || that is also one as far as stages go.
        '|' => Ahead(command, at, '|') ? "||" : "|",
        _ => null,
    };

    /// <summary>
    /// A redirection. The file descriptor in front of one falls out as a word of
    /// its own -- <c>2&gt;/dev/null</c> scans as <c>2</c>, <c>&gt;</c>,
    /// <c>/dev/null</c> -- which is enough, because what the policy asks is
    /// whether anything is being written and to where.
    /// </summary>
    private static string? Redirection(string command, int at) => command[at] switch
    {
        '>' => Ahead(command, at, '>') ? ">>" : ">",
        '<' => Ahead(command, at, '<') ? "<<" : "<",
        _ => null,
    };

    private static bool Ahead(string command, int at, char c) => at + 1 < command.Length && command[at + 1] == c;
}
