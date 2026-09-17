using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// What the assistant writes into a pane it is working in.
///
/// The commands themselves go over the exec channel, as they always have, so
/// their output and exit status are exact. This is the other half: the window
/// showing that host says what is happening in it, so a run across a rack is
/// something you watch rather than something you read about afterwards.
///
/// Dimmed and marked at both ends, so nothing here can be mistaken for the
/// shell's own output. And nothing here is sent: it arrives on the path the
/// server's own output arrives on, which is what TerminalSession.Show is for.
/// </summary>
internal static class Narration
{
    /// <summary>
    /// How much output is echoed into the pane before it is cut short.
    ///
    /// The model may have asked for up to eight thousand characters. That is
    /// the right amount for something reading it to decide what to do next,
    /// and far too much to push through somebody's scrollback: what a person
    /// watching wants is to see that it ran and roughly what came back.
    /// </summary>
    private const int Lines = 12;

    /// <summary>Dim, so the shell's own output stays the brighter thing in the pane.</summary>
    private const string Dim = "\u001b[2m";

    private const string Plain = "\u001b[0m";

    /// <summary>The line written when a command starts.</summary>
    internal static string Starting(FleetStep step) =>
        $"\r\n{Dim}\u2500\u2500 assistant \u00b7 {step.Command}{Plain}\r\n";

    /// <summary>What is written when it comes back: the output, then how it ended.</summary>
    internal static string Finished(FleetStep step)
    {
        var lines = step.Output.Replace("\r\n", "\n").Split('\n');
        var shown = string.Join("\r\n", lines.Take(Lines));
        var rest = lines.Length > Lines
            ? $"\r\n{Dim}   \u2026 {lines.Length - Lines} more lines, in the assistant{Plain}"
            : "";

        var ended = step.ExitStatus is { } status
            ? $"{Dim}\u2500\u2500 exit {status}{Plain}"
            : $"{Dim}\u2500\u2500 it did not finish{Plain}";

        return step.Output.Length == 0
            ? $"{ended}\r\n"
            : $"{shown}{rest}\r\n{ended}\r\n";
    }
}
