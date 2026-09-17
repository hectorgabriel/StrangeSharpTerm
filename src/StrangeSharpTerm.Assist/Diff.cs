using System.Text;

namespace StrangeSharpTerm.Assist;

/// <summary>What a write would change.</summary>
/// <param name="Text">
/// The change as a person reads it: <c>-</c> for what goes, <c>+</c> for what
/// arrives, with a little of what stays around each. Empty when nothing changes.
/// </param>
/// <param name="Summarised">
/// The change was too large to show line by line, so <see cref="Text"/> says how
/// much moves rather than showing it. A diff nobody can read is not a gate.
/// </param>
public sealed record FileDiff(int Added, int Removed, string Text, bool Summarised = false)
{
    public bool IsEmpty => Added == 0 && Removed == 0;

    /// <summary>One line for a row: "3 lines added, 1 removed".</summary>
    public string Summary => (Added, Removed) switch
    {
        (0, 0) => "nothing changes",
        (_, 0) => $"{Lines(Added)} added",
        (0, _) => $"{Lines(Removed)} removed",
        _ => $"{Lines(Added)} added, {Removed} removed",
    };

    private static string Lines(int count) => count == 1 ? "1 line" : $"{count} lines";
}

/// <summary>
/// The difference between what a file holds and what something wants to put
/// there.
///
/// It exists for the gate. "The assistant would like to write nginx.conf" is not
/// a question anyone can answer; the same question with the three lines that
/// change underneath it is. Approving a write without seeing it is approving a
/// file you have not read.
///
/// <para>
/// Deliberately small. This is not <c>git diff</c> — there is no rename
/// detection, no word-level colouring, and one hunk where git would emit
/// several. What it has to be is quick, bounded, and never wrong about what
/// changes.
/// </para>
/// </summary>
public static class Diff
{
    /// <summary>
    /// How many changed lines are compared line by line at all.
    ///
    /// The comparison is quadratic in this number, and it is running while a
    /// person waits at a gate. Past it the answer is a count, which is a true
    /// thing that costs nothing: a write that replaces two thousand lines is a
    /// rewrite, and reading it line by line would not change the decision.
    /// </summary>
    private const int MaxCompared = 400;

    /// <summary>Lines of unchanged text shown around a change.</summary>
    private const int Context = 3;

    /// <summary>How much of the diff is shown, before the rest is counted instead.</summary>
    private const int MaxShown = 160;

    public static FileDiff Between(string before, string after)
    {
        var old = Lines(before);
        var recent = Lines(after);

        // Everything that matches at each end is not part of the change, and
        // taking it off first is what makes a one-line edit to a long file
        // cheap to compare.
        var head = 0;
        while (head < old.Length && head < recent.Length && old[head] == recent[head])
            head++;

        var tail = 0;
        while (tail < old.Length - head && tail < recent.Length - head
               && old[^(tail + 1)] == recent[^(tail + 1)])
            tail++;

        var oldMiddle = old[head..(old.Length - tail)];
        var newMiddle = recent[head..(recent.Length - tail)];

        if (oldMiddle.Length == 0 && newMiddle.Length == 0)
            return new FileDiff(0, 0, "");

        if (oldMiddle.Length > MaxCompared || newMiddle.Length > MaxCompared)
            return new FileDiff(
                newMiddle.Length,
                oldMiddle.Length,
                $"{oldMiddle.Length} lines from line {head + 1} are replaced by {newMiddle.Length} lines.",
                Summarised: true);

        var script = Script(oldMiddle, newMiddle);
        var added = script.Count(step => step.Kind == '+');
        var removed = script.Count(step => step.Kind == '-');

        // The context comes from the text that was trimmed off, which is the
        // same text on both sides -- that is what made it trimmable.
        var before3 = old[Math.Max(0, head - Context)..head];
        var after3 = old[(old.Length - tail)..Math.Min(old.Length, old.Length - tail + Context)];

        var text = new StringBuilder();
        foreach (var line in before3)
            text.Append("  ").AppendLine(line);

        var shown = 0;
        foreach (var step in script)
        {
            if (shown == MaxShown)
            {
                text.AppendLine($"… {script.Count - shown} more changed lines …");
                break;
            }
            text.Append(step.Kind).Append(' ').AppendLine(step.Line);
            shown++;
        }

        foreach (var line in after3)
            text.Append("  ").AppendLine(line);

        return new FileDiff(added, removed, text.ToString().TrimEnd('\n', '\r'));
    }

    /// <summary>One step of the edit script: a line that stays, goes, or arrives.</summary>
    private readonly record struct Step(char Kind, string Line);

    /// <summary>
    /// The shortest way from one block of lines to the other, by the usual
    /// longest-common-subsequence table.
    ///
    /// Bounded by <see cref="MaxCompared"/> before it is called, which is what
    /// makes a table this shape affordable.
    /// </summary>
    private static List<Step> Script(string[] old, string[] recent)
    {
        var lengths = new int[old.Length + 1, recent.Length + 1];
        for (var left = old.Length - 1; left >= 0; left--)
        {
            for (var right = recent.Length - 1; right >= 0; right--)
            {
                lengths[left, right] = old[left] == recent[right]
                    ? lengths[left + 1, right + 1] + 1
                    : Math.Max(lengths[left + 1, right], lengths[left, right + 1]);
            }
        }

        List<Step> script = [];
        var x = 0;
        var y = 0;
        while (x < old.Length && y < recent.Length)
        {
            if (old[x] == recent[y])
            {
                script.Add(new Step(' ', old[x]));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                script.Add(new Step('-', old[x]));
                x++;
            }
            else
            {
                script.Add(new Step('+', recent[y]));
                y++;
            }
        }

        while (x < old.Length)
            script.Add(new Step('-', old[x++]));
        while (y < recent.Length)
            script.Add(new Step('+', recent[y++]));

        return script;
    }

    /// <summary>
    /// Lines, with the line endings taken off first.
    ///
    /// A file read from a Unix host and a text box on a Windows machine end
    /// their lines differently, and a diff that showed every line as changed for
    /// that reason would be worse than no diff at all.
    /// </summary>
    private static string[] Lines(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');
}
