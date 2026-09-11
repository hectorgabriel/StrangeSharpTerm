using System.Text;

namespace StrangeSharpTerm.Store;

/// <summary>One <c>keyword value…</c> line inside an ssh_config block.</summary>
/// <param name="Keyword">The keyword exactly as the user typed it, casing preserved.</param>
/// <param name="Arguments">Values with quoting resolved. <c>LocalForward 8080 localhost:80</c> yields two.</param>
/// <param name="RawLine">
/// The original line, verbatim, so the file can be rewritten without reformatting
/// parts that were not touched.
/// </param>
public sealed record SshConfigEntry(string Keyword, IReadOnlyList<string> Arguments, string RawLine, int LineNumber)
{
    /// <summary>ssh_config keywords are case-insensitive; compare against this.</summary>
    public string NormalizedKeyword => Keyword.ToLowerInvariant();
}

/// <summary>What opens an ssh_config section.</summary>
public abstract record SshConfigHeader
{
    private SshConfigHeader() { }

    /// <summary>Entries before any <c>Host</c> or <c>Match</c> line. Rare but legal, and they apply to every host.</summary>
    public sealed record Global : SshConfigHeader;

    public sealed record Host(IReadOnlyList<string> Patterns) : SshConfigHeader;

    /// <summary>
    /// Criteria are preserved verbatim and never interpreted. Their evaluation
    /// depends on runtime state (exec, originalhost, the final pass) that cannot be
    /// reproduced faithfully.
    /// </summary>
    public sealed record Match(IReadOnlyList<string> Criteria) : SshConfigHeader;
}

/// <summary>A <c>Host</c> or <c>Match</c> section, or the implicit section before the first header.</summary>
public sealed record SshConfigBlock(SshConfigHeader Header, string? HeaderRawLine, IReadOnlyList<SshConfigEntry> Entries)
{
    /// <summary>
    /// First argument list for <paramref name="keyword"/>, or null. ssh honours the
    /// <em>first</em> occurrence of most keywords, so later duplicates are ignored.
    /// </summary>
    public IReadOnlyList<string>? ArgumentsFor(string keyword) =>
        Entries.FirstOrDefault(e => e.NormalizedKeyword == keyword.ToLowerInvariant())?.Arguments;

    /// <summary>Every occurrence, for keywords like <c>IdentityFile</c> that legitimately repeat and accumulate.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AllArgumentsFor(string keyword) =>
        [.. Entries.Where(e => e.NormalizedKeyword == keyword.ToLowerInvariant()).Select(e => e.Arguments)];
}

public sealed record SshConfigFile(IReadOnlyList<SshConfigBlock> Blocks)
{
    /// <summary>Paths named by <c>Include</c> directives, in file order, before glob expansion.</summary>
    public IReadOnlyList<string> IncludePaths =>
        [.. Blocks.SelectMany(b => b.Entries).Where(e => e.NormalizedKeyword == "include").SelectMany(e => e.Arguments)];
}

/// <summary>
/// Lexer and parser for OpenSSH client configuration files.
///
/// Purely textual: it takes a string and returns structure, resolving no
/// <c>Include</c> directives and touching no filesystem, so the whole grammar is
/// testable from fixtures.
/// </summary>
public static class SshConfigParser
{
    public static SshConfigFile Parse(string text)
    {
        var blocks = new List<SshConfigBlock>();
        SshConfigHeader header = new SshConfigHeader.Global();
        string? headerLine = null;
        var entries = new List<SshConfigEntry>();
        var lineNumber = 0;

        foreach (var rawLine in Lines(text))
        {
            lineNumber++;
            if (Tokenize(rawLine) is not var (keyword, arguments))
                continue;

            switch (keyword.ToLowerInvariant())
            {
                case "host":
                    Close();
                    (header, headerLine) = (new SshConfigHeader.Host(arguments), rawLine);
                    break;
                case "match":
                    Close();
                    (header, headerLine) = (new SshConfigHeader.Match(arguments), rawLine);
                    break;
                default:
                    entries.Add(new SshConfigEntry(keyword, arguments, rawLine, lineNumber));
                    break;
            }
        }

        Close();
        return new SshConfigFile(blocks);

        void Close()
        {
            // The implicit global block is only emitted if it collected something.
            if (entries.Count > 0 || header is not SshConfigHeader.Global)
                blocks.Add(new SshConfigBlock(header, headerLine, [.. entries]));
            entries.Clear();
        }
    }

    /// <summary>
    /// Splits one line into its keyword and arguments, or null for blank and
    /// comment-only lines.
    ///
    /// Handles the three separator forms ssh accepts (<c>Port 22</c>, <c>Port=22</c>
    /// and <c>Port = 22</c>) along with double-quoted arguments that may contain
    /// spaces.
    /// </summary>
    internal static (string Keyword, IReadOnlyList<string> Arguments)? Tokenize(string line)
    {
        var index = 0;
        SkipWhitespace();
        if (index == line.Length || line[index] == '#')
            return null;

        // The keyword runs until whitespace or the '=' separator.
        var keywordStart = index;
        while (index < line.Length && line[index] is not (' ' or '\t' or '='))
            index++;
        var keyword = line[keywordStart..index];
        if (keyword.Length == 0)
            return null;

        // Exactly one optional '=' may sit between keyword and value.
        SkipWhitespace();
        if (index < line.Length && line[index] == '=')
        {
            index++;
            SkipWhitespace();
        }

        var arguments = new List<string>();
        var token = new StringBuilder();
        var inQuotes = false;
        for (; index < line.Length; index++)
        {
            var c = line[index];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            // A '#' outside quotes starts a trailing comment.
            if (c == '#' && !inQuotes)
                break;
            if (c is ' ' or '\t' && !inQuotes)
            {
                if (token.Length > 0)
                {
                    arguments.Add(token.ToString());
                    token.Clear();
                }
                continue;
            }
            token.Append(c);
        }
        if (token.Length > 0)
            arguments.Add(token.ToString());

        return (keyword, arguments);

        void SkipWhitespace()
        {
            while (index < line.Length && line[index] is ' ' or '\t')
                index++;
        }
    }

    /// <summary>
    /// Lines split on the characters Swift's <c>CharacterSet.newlines</c> names,
    /// with one deliberate difference: CRLF is a single break. The Swift parser split
    /// on each character separately, so a file saved with Windows line endings grew
    /// a phantom blank line after every real one and every line number after the
    /// first came out wrong.
    /// </summary>
    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\v' or '\f' or '\r' or '\u0085' or '\u2028' or '\u2029'))
                continue;
            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        yield return text[start..];
    }
}
