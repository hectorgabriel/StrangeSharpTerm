namespace StrangeSharpTerm.Assist;

/// <summary>One piece of an answer: prose, or a fenced block.</summary>
public abstract record AnswerBlock
{
    private AnswerBlock() { }

    public sealed record Prose(string Text) : AnswerBlock
    {
        /// <summary>
        /// The paragraph broken into its lines, each with its markup read.
        ///
        /// Here rather than in the pane because it is the same answer wherever
        /// it is shown, and because a parser with no window around it is one a
        /// test can hold to its word.
        /// </summary>
        public IReadOnlyList<ProseLine> Lines => Markdown.Lines(Text);
    }

    /// <param name="Language">The fence's tag, or null when it had none.</param>
    public sealed record Code(string Text, string? Language) : AnswerBlock
    {
        /// <summary>
        /// Whether this block gets the button that types it into the terminal.
        ///
        /// Only a block the model tagged as shell. An untagged fence does not
        /// qualify: a model that did not say what it was writing has not earned
        /// a button that puts it on a server.
        /// </summary>
        public bool IsShell => Language is { } tag && ShellTags.Contains(tag);

        /// <summary>
        /// The block as it would be typed: prompts stripped, because a pasted
        /// <c>$</c> is a command not found.
        /// </summary>
        public string Staged => string.Join('\n',
            Text.Split('\n').Select(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith("$ ", StringComparison.Ordinal)
                    || trimmed.StartsWith("# ", StringComparison.Ordinal)
                        ? trimmed[2..]
                        : line;
            })).Trim();
    }

    private static readonly HashSet<string> ShellTags = new(
        ["sh", "bash", "shell", "zsh", "ksh", "console", "terminal", "shell-session", "bash-session", "shellsession"],
        StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Splits an answer into prose and fenced blocks.
///
/// Written rather than taken from a Markdown library because the one question
/// being asked -- which of these blocks may be staged into a live terminal --
/// is not one a general renderer answers, and the rest of the syntax does not
/// matter here.
/// </summary>
public static class Fences
{
    public static IReadOnlyList<AnswerBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return [];

        List<AnswerBlock> blocks = [];
        List<string> prose = [];
        List<string>? code = null;
        string? language = null;

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var opening = line.TrimStart();
            if (!opening.StartsWith("```", StringComparison.Ordinal))
            {
                (code ?? prose).Add(line);
                continue;
            }

            if (code is null)
            {
                FlushProse();
                var tag = opening[3..].Trim();
                language = tag.Length == 0 ? null : tag.Split(' ')[0];
                code = [];
                continue;
            }

            blocks.Add(new AnswerBlock.Code(string.Join('\n', code).TrimEnd(), language));
            (code, language) = (null, null);
        }

        // A fence the model never closed. Its contents are still the answer, so
        // they are kept -- as a block, which cannot be run without a tag.
        if (code is not null)
            blocks.Add(new AnswerBlock.Code(string.Join('\n', code).TrimEnd(), language));
        FlushProse();

        return blocks;

        void FlushProse()
        {
            var text = string.Join('\n', prose).Trim();
            prose.Clear();
            if (text.Length > 0)
                blocks.Add(new AnswerBlock.Prose(text));
        }
    }

    /// <summary>Every shell block in an answer, in order. What the pane puts a button beside.</summary>
    public static IReadOnlyList<AnswerBlock.Code> ShellBlocks(string? markdown) =>
        [.. Parse(markdown).OfType<AnswerBlock.Code>().Where(block => block.IsShell)];
}
