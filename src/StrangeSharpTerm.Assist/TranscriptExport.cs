using System.Globalization;
using System.Text.RegularExpressions;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// A conversation as Markdown, for somewhere other than this window: an
/// incident's notes, a ticket, a message to whoever is on call next.
///
/// Built from the transcript -- what the person saw -- and not from what was
/// sent, which carries the context block and the recap and is a request rather
/// than a record. Secrets stay as the markers they became before anything
/// reached a provider: the export is a thing that leaves the window, and this
/// app does not put back a value it took out except into a command or a file
/// a person approved.
/// </summary>
public static partial class TranscriptExport
{
    /// <param name="title">What the conversation was about: a host's name, or the hosts of a run.</param>
    /// <param name="about">Lines for under the title: who answered, what it cost.</param>
    public static string Markdown(
        IReadOnlyList<TranscriptEntry> entries,
        string title,
        DateTimeOffset when,
        IEnumerable<string>? about = null)
    {
        List<string> lines =
        [
            $"# {title}",
            "",
            $"Exported {when.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)}.",
        ];
        lines.AddRange((about ?? []).Where(line => line.Length > 0));
        lines.Add("Secrets were taken out before anything reached the provider, and they stay out here.");

        foreach (var entry in entries)
        {
            lines.Add("");
            switch (entry)
            {
                case TranscriptEntry.Question question:
                    lines.Add("## " + OneLine(question.Text));
                    // The heading is one line; a question that ran to several
                    // is kept whole underneath it.
                    if (question.Text.Contains('\n'))
                    {
                        lines.Add("");
                        lines.AddRange(question.Text.Trim().Split('\n').Select(line => $"> {line}"));
                    }
                    break;

                case TranscriptEntry.Step step:
                    lines.AddRange(Step(step));
                    break;

                case TranscriptEntry.Answer { Markdown.Length: > 0 } answer:
                    lines.Add(answer.Markdown.Trim());
                    break;

                case TranscriptEntry.Answer:
                    // A turn that only asked for a command says nothing.
                    lines.RemoveAt(lines.Count - 1);
                    break;

                case TranscriptEntry.Note note:
                    lines.AddRange(note.Text.Trim().Split('\n').Select(line => line.Length == 0 ? ">" : $"> *{line.Trim()}*"));
                    break;
            }
        }

        // One ending, whatever the machine: this goes to a ticket or a chat,
        // and the far end has no opinion about where the window was running.
        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    private static IEnumerable<string> Step(TranscriptEntry.Step step)
    {
        // A file's line already says which machine when it is this one.
        var where = step.IsTool || (step.IsFile && step.Host == "this machine")
            ? step.Command
            : step.IsFile ? $"{step.Command} on {step.Host}" : step.Host;
        yield return $"**{Ended(step)}** · {where}" + (step.Why.Length > 0 && !step.IsTool ? $" — {OneLine(step.Why)}" : "");

        // A command, or what a connected tool was sent, which for a tool is
        // where its arguments are kept.
        if (!step.IsFile)
        {
            foreach (var line in step.IsTool ? Fenced(step.Why, "json") : Fenced(step.Command, "sh"))
                yield return line;
        }

        if (step.Detail.Length > 0)
        {
            foreach (var line in Fenced(step.Detail, "diff"))
                yield return line;
        }

        if (step.Output.Trim().Length > 0)
        {
            foreach (var line in Fenced(step.Output.TrimEnd(), ""))
                yield return line;
        }
    }

    /// <summary>How it ended, in the row's own words.</summary>
    private static string Ended(TranscriptEntry.Step step) => step.State switch
    {
        StepState.Ran when step.ExitStatus is { } status => step.RanUnattended ? $"Ran, exit {status}" : $"Allowed, exit {status}",
        StepState.Ran => step.RanUnattended ? "Ran" : "Allowed",
        StepState.Refused => "Refused",
        StepState.TimedOut when step.MayStillBeRunning => "Timed out, may still be running",
        StepState.TimedOut => "Timed out, stopped",
        StepState.Failed => "Could not be run",
        StepState.Skipped => "Not run, out of budget",
        StepState.Waiting => "Waiting for an answer",
        StepState.Queued => "Queued",
        _ => "Running",
    };

    /// <summary>
    /// A fenced block that whatever is inside it cannot close: one backtick
    /// longer than the longest run of them in the text. Command output is
    /// whatever the server printed, and a README it cat'ed has fences of its
    /// own.
    /// </summary>
    internal static IEnumerable<string> Fenced(string text, string language)
    {
        var longest = Backticks().Matches(text).Select(run => run.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        yield return fence + language;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            yield return line;
        yield return fence;
    }

    private static string OneLine(string text)
    {
        var first = text.Trim().Split('\n')[0].Trim();
        return text.Trim().Contains('\n') ? first + " …" : first;
    }

    [GeneratedRegex("`+")]
    private static partial Regex Backticks();
}
