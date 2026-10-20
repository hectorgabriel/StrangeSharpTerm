using System.Text;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// Everything about a host that may leave the machine.
///
/// The alias you chose, what <c>uname</c> said, the metrics the dashboard's own
/// probe collected, and the tail of the terminal beside the pane. There is no
/// hostname, address, username or key material here, and this type has nowhere
/// to put them -- the boundary is structural rather than a habit of the call
/// site, and a test asserts it.
///
/// <see cref="Kernel"/> is <c>uname -srm</c> rather than <c>uname -a</c> for
/// that reason: the long form prints the machine's own hostname.
/// </summary>
public sealed record HostContext
{
    /// <summary>The command that fills in <see cref="Kernel"/>. Deliberately not <c>uname -a</c>.</summary>
    public const string KernelCommand = "uname -srm";

    /// <summary>The name the inventory gives the host. The user chose it, so it is theirs to send.</summary>
    public required string Alias { get; init; }

    /// <summary>Kernel name, release and architecture. Null when the probe did not run or failed.</summary>
    public string? Kernel { get; init; }

    /// <summary>
    /// What the server said about itself, from the same probe the dashboard
    /// runs -- asked when a question is asked, rather than on a timer. A pane
    /// sitting open is not a reason to poll a server.
    /// </summary>
    public ServerMetrics? Metrics { get; init; }

    /// <summary>The tail of the terminal beside the pane, already scrubbed.</summary>
    public string? TerminalTail { get; init; }

    /// <summary>How many secrets <see cref="Redaction"/> took out of the tail.</summary>
    public int Redactions { get; init; }

    /// <summary>
    /// What this conversation was last told about the host, or null when it has
    /// been told nothing yet. <see cref="Render"/> leaves out what that already
    /// said.
    /// </summary>
    /// <remarks>
    /// Every question used to carry the whole block again: the same kernel, the
    /// same hundred and twenty lines of terminal with a few new ones at the
    /// bottom. In a long conversation that is most of what grows besides command
    /// output, and all of it was already in the conversation above.
    ///
    /// The same type, so the boundary is the same: nothing can be in here that
    /// could not be in the block itself. Held one deep -- the conversation's
    /// knowledge is the last full account, not a history of them.
    /// </remarks>
    public HostContext? Since { get; init; }

    /// <summary>
    /// The block as it is sent, and as the pane's disclosure shows it.
    ///
    /// One rendering, used for both. The preview being the request itself rather
    /// than a description of it is what stops the two drifting apart.
    /// </summary>
    public string Render()
    {
        if (Since is { } told)
            return Update(told);

        List<string> lines = [$"Host: {Alias}"];

        if (Kernel is { Length: > 0 } kernel)
            lines.Add($"System: {kernel.Trim()}");

        if (Metrics is { IsEmpty: false } metrics)
        {
            lines.Add("");
            lines.Add("Current state:");
            lines.AddRange(Describe(metrics).Select(line => $"- {line}"));
        }

        if (TerminalTail is { Length: > 0 } tail)
        {
            lines.Add("");
            lines.Add("What the terminal is showing:");
            lines.Add("```");
            // The tail comes from a pty on the far end, which may have sent
            // either ending; what leaves here is one.
            lines.AddRange(tail.Replace("\r\n", "\n").Split('\n'));
            lines.Add("```");
        }

        // Joined with a line feed rather than built with AppendLine, whose
        // ending is the *local* machine's. This block describes a remote host
        // and is read by a provider, and neither has an opinion about the
        // operating system the window happens to be running on -- a Windows user
        // asking about a Linux server was sending CRLF for its output, and the
        // preview and the request have to be one string.
        return string.Join('\n', lines).TrimEnd();
    }

    /// <summary>
    /// Only what changed since the conversation was last told, and a sentence
    /// saying so when nothing did -- an empty block would look like a probe
    /// that failed.
    /// </summary>
    private string Update(HostContext told)
    {
        List<string> lines = [$"Host: {Alias}"];
        var said = false;

        if (Kernel is { Length: > 0 } kernel && kernel.Trim() != told.Kernel?.Trim())
        {
            lines.Add($"System: {kernel.Trim()}");
            said = true;
        }

        // By line, because that is how the model read them: the load average
        // moving is news, the memory total being what it was is not.
        if (Metrics is { IsEmpty: false } metrics)
        {
            var before = told.Metrics is { } earlier ? Describe(earlier).ToHashSet() : [];
            var changed = Describe(metrics).Where(line => !before.Contains(line)).ToArray();
            if (changed.Length > 0)
            {
                lines.Add("");
                lines.Add("Changed since the last question:");
                lines.AddRange(changed.Select(line => $"- {line}"));
                said = true;
            }
        }

        if (TerminalTail is { Length: > 0 } tail)
        {
            var (fresh, continues) = Fresh(told.TerminalTail, tail);
            if (fresh.Count > 0)
            {
                lines.Add("");
                lines.Add(continues
                    ? "New in the terminal since the last question, following on from what it showed then:"
                    : "What the terminal is showing:");
                lines.Add("```");
                lines.AddRange(fresh);
                lines.Add("```");
                said = true;
            }
        }

        if (!said)
            lines.Add("Nothing about the host has changed since the last question.");

        return string.Join('\n', lines).TrimEnd();
    }

    /// <summary>
    /// The lines of the terminal the conversation has not seen.
    ///
    /// The tail is a window that scrolls, so what was sent last time is still
    /// there, moved up by however much has been printed since. Found by
    /// sliding it up until it lines up with the top of what is there now --
    /// all but its last line, which is usually the prompt, and a prompt with a
    /// command typed into it is the same line changed rather than a new one.
    /// So that line goes again, with whatever follows it.
    ///
    /// When nothing lines up -- the screen was cleared, or the pane follows a
    /// different shell now -- the whole tail goes, because a piece of it would
    /// read as following on from something it does not.
    /// </summary>
    /// <returns>The lines to send, and whether they follow on from the last ones.</returns>
    internal static (IReadOnlyList<string> Lines, bool Continues) Fresh(string? before, string now)
    {
        var current = Lines(now);
        if (before is not { Length: > 0 })
            return (current, false);

        var earlier = Lines(before);
        var kept = earlier.Length - 1;
        for (var shift = 0; shift < kept; shift++)
        {
            var overlap = kept - shift;
            if (overlap > current.Length || !earlier.AsSpan(shift, overlap).SequenceEqual(current.AsSpan(0, overlap)))
                continue;

            var fresh = current[overlap..];
            // The prompt it was sitting at, unchanged, is nothing new.
            return fresh is [var only] && only == earlier[^1] ? ([], true) : (fresh, true);
        }

        return current.SequenceEqual(earlier) ? ([], true) : (current, false);
    }

    private static string[] Lines(string tail) => tail.Replace("\r\n", "\n").Split('\n');

    /// <summary>Metrics as sentences, leaving out whatever the server did not report.</summary>
    internal static IEnumerable<string> Describe(ServerMetrics metrics)
    {
        if (metrics.Uptime is { Length: > 0 } uptime)
            yield return $"up {uptime}";

        if (metrics.LoadAverages is { Count: >= 3 } load)
            yield return $"load average {load[0]:0.00}, {load[1]:0.00}, {load[2]:0.00}";

        if (metrics.MemoryTotalBytes is { } memoryTotal && metrics.MemoryUsedBytes is { } memoryUsed)
            yield return $"memory {Size(memoryUsed)} of {Size(memoryTotal)} used{Percent(metrics.MemoryFraction)}";

        if (metrics.DiskTotalBytes is { } diskTotal && metrics.DiskUsedBytes is { } diskUsed)
            yield return $"root filesystem {Size(diskUsed)} of {Size(diskTotal)} used{Percent(metrics.DiskFraction)}";

        foreach (var container in metrics.Containers)
            yield return $"container {container.Name}: {container.Status}";
    }

    /// <summary>
    /// A percentage, written out rather than formatted: the invariant culture's
    /// own percent format puts a space before the sign, and "75 %" in a block
    /// going to a model is a small oddity with no upside.
    /// </summary>
    private static string Percent(double? fraction) =>
        fraction is { } value ? $" ({Math.Round(value * 100)}%)" : "";

    /// <summary>
    /// Bytes as a person reads them. Powers of 1024 and the units <c>df -h</c>
    /// prints, so the figure in the block matches the figure the model will see
    /// if it runs the command itself.
    /// </summary>
    internal static string Size(ulong bytes)
    {
        string[] units = ["B", "K", "M", "G", "T", "P"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes}B" : $"{value:0.#}{units[unit]}";
    }
}
