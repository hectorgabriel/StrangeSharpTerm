using System.Text;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// What a conversation still knows about the exchanges it stopped sending.
///
/// Compacting a conversation drops its oldest exchanges whole, and on a long
/// plan the oldest is usually the one that found things out: what phase one
/// saw is what phase nine is built on. A note on screen saying it was left out
/// did nothing for the model, which went on as though it had never been asked.
///
/// So each dropped exchange leaves a few lines behind: what was asked, what ran
/// where and how it ended, and what the answer said. Built from the transcript
/// rather than asked of the model, because a summary that costs a request and
/// can be wrong is a poor trade for one that is free and cannot be. No command
/// output goes into it: that is what made the exchange large, and the answer
/// is the account of it the model already wrote.
/// </summary>
internal sealed class Recap
{
    /// <summary>Kept newest-last. Each is one dropped exchange.</summary>
    private readonly List<string> _exchanges = [];

    public bool IsEmpty => _exchanges.Count == 0;

    public void Clear() => _exchanges.Clear();

    /// <summary>
    /// Adds the account of one dropped exchange, and lets go of the oldest
    /// accounts once together they are more than
    /// <see cref="AssistLimits.MaxRecapCharacters"/>: a recap that grew without
    /// limit would be the problem compaction exists to solve.
    /// </summary>
    public void Add(IEnumerable<TranscriptEntry> exchange)
    {
        _exchanges.Add(Of(exchange));
        while (_exchanges.Count > 1 && _exchanges.Sum(text => text.Length) > AssistLimits.MaxRecapCharacters)
            _exchanges.RemoveAt(0);
    }

    /// <summary>
    /// What is sent: the conversation, with the recap at the front of its first
    /// message.
    ///
    /// In that message rather than one of its own. The first thing left is
    /// always a question, a user turn, and a recap as another user turn before
    /// it is two in a row -- which one provider merges, another refuses, and a
    /// third answers oddly. The stored conversation is left as it is; the recap
    /// is put on at the last moment, so the next compaction drops that question
    /// cleanly and the recap moves to the one after it.
    /// </summary>
    public IReadOnlyList<AssistMessage> Lead(IReadOnlyList<AssistMessage> conversation)
    {
        if (IsEmpty || conversation is not [{ Role: AssistRole.User, ToolResults.Count: 0 } first, ..])
            return [.. conversation];

        var told = string.Join("\n\n", [
            "Earlier in this conversation -- no longer sent in full, so this is what remains of it:",
            .. _exchanges,
            "The conversation continues from here.",
        ]);
        return [first with { Text = $"{told}\n\n{first.Text}" }, .. conversation.Skip(1)];
    }

    /// <summary>The size the recap adds to a request, for a bound that has to count it.</summary>
    public int Size => IsEmpty ? 0 : _exchanges.Sum(text => text.Length) + 120;

    internal static string Of(IEnumerable<TranscriptEntry> exchange)
    {
        var lines = new List<string>();
        var answer = "";
        foreach (var entry in exchange)
        {
            switch (entry)
            {
                case TranscriptEntry.Question question:
                    lines.Add($"Asked: {Clip(question.Text, 300)}");
                    break;

                case TranscriptEntry.Step step:
                    lines.Add($"- {Where(step)}{Clip(step.Command, 200)} — {Ended(step)}");
                    break;

                case TranscriptEntry.Answer { Markdown.Length: > 0 } said:
                    answer = said.Markdown;
                    break;
            }
        }

        // The last thing it said, which is the conclusion; what it said on the
        // way there is in the commands.
        if (answer.Length > 0)
            lines.Add($"Answered: {Clip(answer, 600)}");

        return string.Join('\n', lines);
    }

    private static string Where(TranscriptEntry.Step step) =>
        step.IsTool ? "" : step.Host.Length > 0 ? $"on {step.Host}: " : "";

    private static string Ended(TranscriptEntry.Step step) => step.State switch
    {
        StepState.Ran when step.ExitStatus is { } status => $"exit {status}",
        StepState.Ran => "ran",
        StepState.Refused => "refused by the user",
        StepState.TimedOut when step.MayStillBeRunning => "timed out, may still be running",
        StepState.TimedOut => "timed out, stopped",
        StepState.Failed => "could not be run",
        StepState.Skipped => "not run, out of budget",
        _ => "did not finish",
    };

    /// <summary>One line, and not much of it.</summary>
    private static string Clip(string text, int limit)
    {
        var flat = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
            flat.Append(c is '\n' or '\r' or '\t' ? ' ' : c);
        var line = flat.ToString();
        return line.Length <= limit ? line : string.Concat(line.AsSpan(0, limit), "…");
    }
}
