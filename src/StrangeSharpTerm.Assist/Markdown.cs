namespace StrangeSharpTerm.Assist;

/// <summary>How a run of text inside a line is marked up.</summary>
public enum InlineStyle
{
    Plain,

    /// <summary><c>**like this**</c>.</summary>
    Strong,

    /// <summary><c>*like this*</c>.</summary>
    Emphasis,

    /// <summary><c>`like this`</c>, which an answer about a server uses constantly.</summary>
    Code,
}

/// <summary>One run of text inside a line, and what it is.</summary>
public sealed record InlineSpan(string Text, InlineStyle Style = InlineStyle.Plain);

/// <summary>
/// One line of prose: what kind of line it is, and the runs inside it.
/// </summary>
/// <param name="Marker">
/// What a list item is introduced by: null for a paragraph, "•" for a bullet,
/// or the number the model wrote. The number is kept rather than recounted,
/// because a model that starts a list at 3 usually means to.
/// </param>
public sealed record ProseLine(
    IReadOnlyList<InlineSpan> Spans,
    int Heading = 0,
    string? Marker = null,
    int Depth = 0);

/// <summary>
/// As much Markdown as an answer about a server actually uses.
///
/// Written rather than taken from a library for the same reason
/// <see cref="Fences"/> was: this renders into a pane that is 380 points wide
/// beside a terminal, not into a browser. Headings, lists, bold, italic and
/// inline code are what these answers are made of; tables, images, footnotes
/// and reference links are not, and supporting them would be carrying a parser
/// for text nobody sends.
///
/// Anything it does not understand comes through as plain text rather than as
/// an error or as raw syntax, which is the property that matters: a model that
/// writes something unexpected must not produce a pane full of asterisks.
/// </summary>
public static class Markdown
{
    /// <summary>Splits a paragraph block into its lines, each already parsed.</summary>
    public static IReadOnlyList<ProseLine> Lines(string? prose)
    {
        if (string.IsNullOrWhiteSpace(prose))
            return [];

        List<ProseLine> lines = [];
        foreach (var raw in prose.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
                continue;

            var indent = line.Length - line.TrimStart().Length;
            var text = line.TrimStart();

            // A heading: one to six hashes and a space. "#hashtag" is not one.
            var hashes = text.TakeWhile(character => character == '#').Count();
            if (hashes is > 0 and <= 6 && text.Length > hashes && text[hashes] == ' ')
            {
                lines.Add(new ProseLine(Inlines(text[(hashes + 1)..].Trim()), Heading: hashes));
                continue;
            }

            // A bullet: -, * or + and a space. Not "*emphasis*", which has no
            // space after the marker, and not "---", which is a rule.
            if (text.Length > 1 && text[0] is '-' or '*' or '+' && text[1] == ' ')
            {
                lines.Add(new ProseLine(Inlines(text[2..].Trim()), Marker: "•", Depth: indent / 2));
                continue;
            }

            // A numbered item: digits, then . or ), then a space.
            var digits = text.TakeWhile(char.IsAsciiDigit).Count();
            if (digits > 0
                && text.Length > digits + 1
                && text[digits] is '.' or ')'
                && text[digits + 1] == ' ')
            {
                lines.Add(new ProseLine(
                    Inlines(text[(digits + 2)..].Trim()),
                    Marker: text[..(digits + 1)],
                    Depth: indent / 2));
                continue;
            }

            lines.Add(new ProseLine(Inlines(text)));
        }

        return lines;
    }

    /// <summary>
    /// Splits one line into its runs.
    ///
    /// Code first and without recursion, because what is inside backticks is
    /// literal: <c>`rm -rf *`</c> is a command with an asterisk in it, not a
    /// command in italics.
    /// </summary>
    public static IReadOnlyList<InlineSpan> Inlines(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return [];

        List<InlineSpan> spans = [];
        var plain = new System.Text.StringBuilder();
        var at = 0;

        while (at < line.Length)
        {
            var (style, length, text) = Marked(line, at);
            if (style == InlineStyle.Plain)
            {
                plain.Append(line[at]);
                at++;
                continue;
            }

            Flush();
            spans.Add(new InlineSpan(text, style));
            at += length;
        }

        Flush();
        return spans;

        void Flush()
        {
            if (plain.Length == 0)
                return;
            spans.Add(new InlineSpan(plain.ToString()));
            plain.Clear();
        }
    }

    /// <summary>
    /// What starts at this position, if anything: the style, how much of the
    /// line it takes, and the text inside the marks.
    /// </summary>
    private static (InlineStyle Style, int Length, string Text) Marked(string line, int at)
    {
        // Backticks hug nothing: `df -h /` has spaces inside it on purpose.
        if (line[at] == '`')
            return Between(line, at, "`", InlineStyle.Code, hugging: false);

        if (line[at] is '*' or '_')
        {
            var mark = line[at];
            var doubled = at + 1 < line.Length && line[at + 1] == mark;
            return doubled
                ? Between(line, at, new string(mark, 2), InlineStyle.Strong, hugging: true)
                : Between(line, at, mark.ToString(), InlineStyle.Emphasis, hugging: true);
        }

        return (InlineStyle.Plain, 1, "");
    }

    /// <summary>
    /// The text between one mark and the next of the same kind.
    ///
    /// An opening mark with no closing one is not markup: the answer simply
    /// contains an asterisk, and it is shown as one.
    /// </summary>
    /// <param name="hugging">
    /// Whether the marks have to touch their text, as Markdown requires of
    /// emphasis. Without it "2 * 3 * 4" is arithmetic that renders as italics
    /// with its asterisks eaten -- and multiplication turns up in an answer
    /// about disks more often than emphasis does.
    /// </param>
    private static (InlineStyle Style, int Length, string Text) Between(
        string line, int at, string mark, InlineStyle style, bool hugging)
    {
        var from = at + mark.Length;
        var close = line.IndexOf(mark, from, StringComparison.Ordinal);

        // Nothing between the marks is not markup either: ** on its own is two
        // asterisks, and treating it as an empty bold swallows them.
        if (close <= from)
            return (InlineStyle.Plain, 1, "");

        if (hugging && (char.IsWhiteSpace(line[from]) || char.IsWhiteSpace(line[close - 1])))
            return (InlineStyle.Plain, 1, "");

        return (style, close + mark.Length - at, line[from..close]);
    }
}
