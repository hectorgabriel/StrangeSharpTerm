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
    /// The block as it is sent, and as the pane's disclosure shows it.
    ///
    /// One rendering, used for both. The preview being the request itself rather
    /// than a description of it is what stops the two drifting apart.
    /// </summary>
    public string Render()
    {
        var block = new StringBuilder();
        block.Append("Host: ").AppendLine(Alias);

        if (Kernel is { Length: > 0 } kernel)
            block.Append("System: ").AppendLine(kernel.Trim());

        if (Metrics is { IsEmpty: false } metrics)
        {
            block.AppendLine().AppendLine("Current state:");
            foreach (var line in Describe(metrics))
                block.Append("- ").AppendLine(line);
        }

        if (TerminalTail is { Length: > 0 } tail)
        {
            block.AppendLine().AppendLine("What the terminal is showing:");
            block.AppendLine("```").AppendLine(tail).AppendLine("```");
        }

        return block.ToString().TrimEnd();
    }

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
