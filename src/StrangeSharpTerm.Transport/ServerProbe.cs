using System.Globalization;

namespace StrangeSharpTerm.Transport;

/// <summary>
/// A snapshot of a server's state.
///
/// Every field is optional. A probe runs on whatever the server happens to be —
/// Linux, macOS, a container without <c>docker</c> — and reporting a confident
/// zero for something that could not be measured would be worse than reporting
/// nothing.
/// </summary>
public sealed record ServerMetrics
{
    public string? Uptime { get; init; }
    public IReadOnlyList<double>? LoadAverages { get; init; }
    public ulong? MemoryUsedBytes { get; init; }
    public ulong? MemoryTotalBytes { get; init; }
    public ulong? DiskUsedBytes { get; init; }
    public ulong? DiskTotalBytes { get; init; }
    public IReadOnlyList<Container> Containers { get; init; } = [];

    /// <param name="Status">Docker writes "Up 3 hours" or "Exited (0) 2 minutes ago".</param>
    public sealed record Container(string Name, string Status)
    {
        public bool IsRunning => Status.StartsWith("Up", StringComparison.Ordinal);
    }

    public double? MemoryFraction => Fraction(MemoryUsedBytes, MemoryTotalBytes);

    public double? DiskFraction => Fraction(DiskUsedBytes, DiskTotalBytes);

    public bool IsEmpty =>
        Uptime is null && LoadAverages is null && MemoryTotalBytes is null
        && DiskTotalBytes is null && Containers.Count == 0;

    private static double? Fraction(ulong? used, ulong? total) =>
        used is { } u && total is { } t && t > 0 ? (double)u / t : null;
}

/// <summary>
/// Collects <see cref="ServerMetrics"/> from a host.
///
/// One command with delimited sections rather than several round trips: each
/// exec is a channel on the connection, and a dashboard refreshing every few
/// seconds across a dozen hosts would otherwise be a lot of needless work.
/// </summary>
public static class ServerProbe
{
    internal const string Delimiter = "###ST:";

    /// <summary>
    /// The probe. Everything is guarded, because a missing tool must produce an
    /// empty section rather than a failed command.
    /// </summary>
    public static string Command { get; } = string.Join("\n",
        $"echo '{Delimiter}uptime'; uptime 2>/dev/null;",
        $"echo '{Delimiter}load'; cat /proc/loadavg 2>/dev/null || sysctl -n vm.loadavg 2>/dev/null;",
        $"echo '{Delimiter}mem'; free -b 2>/dev/null || {{ sysctl -n hw.memsize 2>/dev/null; vm_stat 2>/dev/null; }};",
        $"echo '{Delimiter}disk'; df -k / 2>/dev/null;",
        $"echo '{Delimiter}docker'; docker ps --format '{{{{.Names}}}}|{{{{.Status}}}}' 2>/dev/null;",
        $"echo '{Delimiter}end'");

    public static ServerMetrics Parse(string output)
    {
        var sections = Sections(output);
        var memory = sections.TryGetValue("mem", out var memoryLines) ? ParseMemory(memoryLines) : null;
        var disk = sections.TryGetValue("disk", out var diskLines) ? ParseDisk(diskLines) : null;

        return new ServerMetrics
        {
            Uptime = sections.GetValueOrDefault("uptime")?.FirstOrDefault() is { } uptime ? ParseUptime(uptime) : null,
            LoadAverages = sections.GetValueOrDefault("load")?.FirstOrDefault() is { } load ? ParseLoad(load) : null,
            MemoryUsedBytes = memory?.Used,
            MemoryTotalBytes = memory?.Total,
            DiskUsedBytes = disk?.Used,
            DiskTotalBytes = disk?.Total,
            Containers = [.. (sections.GetValueOrDefault("docker") ?? []).Select(ParseContainer).OfType<ServerMetrics.Container>()],
        };
    }

    /// <summary>Splits the output into its sections.</summary>
    internal static Dictionary<string, List<string>> Sections(string output)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(Delimiter, StringComparison.Ordinal))
            {
                current = [];
                result[line[Delimiter.Length..]] = current;
            }
            else if (current is not null && line.Trim().Length > 0)
            {
                current.Add(line);
            }
        }
        return result;
    }

    /// <summary>
    /// Reduces <c>uptime</c> to the part people actually read.
    ///
    /// Split on commas and keep the leading duration fields, stopping at the user
    /// count. The two platforms differ — macOS says "43 mins, 2 users" while Linux
    /// says "10 days,  4:33,  2 users" — so cutting at a fixed marker gets one of
    /// them wrong.
    /// </summary>
    internal static string ParseUptime(string line)
    {
        var start = line.IndexOf("up ", StringComparison.Ordinal);
        if (start < 0)
            return line.Trim();

        var duration = line[(start + 3)..]
            .Split(',')
            .Select(field => field.Trim())
            .TakeWhile(field => !field.Contains("user", StringComparison.Ordinal)
                && !field.Contains("load", StringComparison.Ordinal))
            .ToArray();

        return duration.Length == 0 ? line.Trim() : string.Join(", ", duration);
    }

    /// <summary>Handles both <c>/proc/loadavg</c> and macOS's <c>{ 1.85 1.98 2.11 }</c>.</summary>
    internal static IReadOnlyList<double>? ParseLoad(string line)
    {
        var numbers = line.Replace('{', ' ').Replace('}', ' ')
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : (double?)null)
            .OfType<double>()
            .ToArray();

        return numbers.Length >= 3 ? numbers[..3] : null;
    }

    /// <summary><c>free -b</c> on Linux, or <c>hw.memsize</c> plus <c>vm_stat</c> on macOS.</summary>
    internal static (ulong Used, ulong Total)? ParseMemory(IReadOnlyList<string> lines)
    {
        if (lines.FirstOrDefault(l => l.StartsWith("Mem:", StringComparison.Ordinal)) is { } memoryLine)
        {
            // total used free shared buff/cache available
            var fields = Numbers(memoryLine);
            return fields.Length >= 2 ? (fields[1], fields[0]) : null;
        }

        // macOS: the first line is hw.memsize, then vm_stat's page counts.
        if (lines.Count == 0 || !ulong.TryParse(lines[0].Trim(), out var total))
            return null;

        var pageSizeLine = lines.FirstOrDefault(l => l.Contains("page size of", StringComparison.Ordinal));
        var pageSize = pageSizeLine is null ? 0 : Numbers(pageSizeLine).FirstOrDefault();
        if (pageSize == 0)
            return (0, total);

        // Free memory is what is genuinely unused; everything else counts as in
        // use, which is the figure a person means by "memory used".
        var free = Pages("Pages free:") + Pages("Pages speculative:");
        return (total > free * pageSize ? total - free * pageSize : 0, total);

        ulong Pages(string label)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(label, StringComparison.Ordinal));
            if (line is null)
                return 0;
            var digits = new string([.. line.Where(char.IsDigit)]);
            return ulong.TryParse(digits, out var value) ? value : 0;
        }
    }

    /// <summary><c>df -k /</c> — the second line, in 1024-byte blocks.</summary>
    internal static (ulong Used, ulong Total)? ParseDisk(IReadOnlyList<string> lines)
    {
        var line = lines.FirstOrDefault(l => !l.StartsWith("Filesystem", StringComparison.Ordinal));
        if (line is null)
            return null;

        // filesystem, blocks, used, available, ...
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 4 && ulong.TryParse(fields[1], out var total) && ulong.TryParse(fields[2], out var used)
            ? (used * 1024, total * 1024)
            : null;
    }

    internal static ServerMetrics.Container? ParseContainer(string line)
    {
        var separator = line.IndexOf('|');
        if (separator <= 0)
            return null;
        return new ServerMetrics.Container(line[..separator].Trim(), line[(separator + 1)..].Trim());
    }

    private static ulong[] Numbers(string line) =>
        [.. line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => ulong.TryParse(token, out var value) ? value : (ulong?)null)
            .OfType<ulong>()];
}
