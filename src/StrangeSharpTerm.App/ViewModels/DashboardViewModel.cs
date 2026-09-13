using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>How full something is, and how much that matters.</summary>
public enum Pressure
{
    /// <summary>Ordinary. Drawn in the accent, as the Swift dashboard draws memory.</summary>
    Fine,

    /// <summary>Three quarters full: worth noticing before it is a problem.</summary>
    Warning,

    /// <summary>Nine tenths. The thing people wanted the dashboard for.</summary>
    Critical,
}

/// <summary>One bar: what it measures, how full, and in what colour.</summary>
public sealed record Gauge(string Label, double Fraction, string Detail, Pressure Pressure)
{
    public double Percent => Math.Round(Fraction * 100);

    public string PercentText => $"{Percent:0}%";
}

/// <summary>
/// What a server says about itself: uptime, load, memory, disk, containers.
///
/// Asked for, never assumed. Selecting a host in the sidebar deliberately does
/// not connect to it — a stray click in a list of production servers should not
/// open a session on one — and a dashboard that probed on selection would undo
/// that decision quietly. So the card starts empty and says how to fill it.
/// </summary>
public sealed partial class DashboardViewModel(IServerHealth health, Func<Action, Task>? offThread = null)
    : ObservableObject
{
    /// <summary>How many samples the sparkline remembers. Enough to show a trend, not a history.</summary>
    public const int Samples = 24;

    private readonly Func<Action, Task> _run = offThread ?? (work => Task.Run(work));
    private readonly List<double> _load = [];

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Why the last attempt failed, for the card to say in place.</summary>
    [ObservableProperty]
    public partial string? Failure { get; private set; }

    [ObservableProperty]
    public partial ServerMetrics? Metrics { get; private set; }

    /// <summary>Nothing has been asked yet, which is how a dashboard starts.</summary>
    public bool HasAnswer => Metrics is not null;

    public string? Uptime => Metrics?.Uptime;

    /// <summary>The three load averages as a server says them, or null when it did not.</summary>
    public string? Load =>
        Metrics?.LoadAverages is { Count: > 0 } averages
            ? string.Join("  ", averages.Select(average => average.ToString("0.00", CultureInfo.InvariantCulture)))
            : null;

    /// <summary>
    /// The one-minute load over time, oldest first: the sparkline's own data.
    ///
    /// A copy each time, deliberately. The list behind it is appended to in
    /// place, and a binding handed the same instance twice sees no change and
    /// redraws nothing — which is exactly what the line did on the second
    /// refresh: new numbers above it, no line under them.
    /// </summary>
    public IReadOnlyList<double> LoadHistory => [.. _load];

    public Gauge? Memory => Bar("Memory", Metrics?.MemoryFraction, Metrics?.MemoryUsedBytes, Metrics?.MemoryTotalBytes);

    public Gauge? Disk => Bar("Disk", Metrics?.DiskFraction, Metrics?.DiskUsedBytes, Metrics?.DiskTotalBytes);

    public IReadOnlyList<ServerMetrics.Container> Containers => Metrics?.Containers ?? [];

    /// <summary>
    /// True when the server answered but had nothing to say — a container with
    /// no <c>uptime</c>, <c>free</c> or <c>df</c>. Different from not having asked.
    /// </summary>
    public bool AnsweredWithNothing => Metrics is { IsEmpty: true };

    /// <summary>Asks the server how it is.</summary>
    [RelayCommand]
    public async Task Refresh()
    {
        IsBusy = true;
        Failure = null;
        ServerMetrics? metrics = null;

        try
        {
            await _run(() => metrics = health.Collect());
        }
        catch (Exception e)
        {
            Failure = SshFailure.Classify(e).Summary;
            IsBusy = false;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // The sparkline is built from what this pane has seen, because a server
        // does not keep a history for us: one-minute load, oldest first.
        if (metrics?.LoadAverages is { Count: > 0 } averages)
        {
            _load.Add(averages[0]);
            if (_load.Count > Samples)
                _load.RemoveAt(0);
        }

        Metrics = metrics;
        OnPropertyChanged(nameof(HasAnswer));
        OnPropertyChanged(nameof(Uptime));
        OnPropertyChanged(nameof(Load));
        OnPropertyChanged(nameof(LoadHistory));
        OnPropertyChanged(nameof(Memory));
        OnPropertyChanged(nameof(Disk));
        OnPropertyChanged(nameof(Containers));
        OnPropertyChanged(nameof(AnsweredWithNothing));
    }

    /// <summary>
    /// A size the way a dashboard says it: two significant figures and the unit
    /// people think in. 6.01 GB, not 6455980032 bytes.
    /// </summary>
    public static string Bytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit switch
        {
            0 => $"{bytes} B",
            _ => $"{size.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}",
        };
    }

    /// <summary>
    /// How much a fullness matters. The thresholds are the ones a person would
    /// use: three quarters is worth noticing, nine tenths is why they looked.
    /// </summary>
    public static Pressure PressureOf(double fraction) => fraction switch
    {
        >= 0.9 => Pressure.Critical,
        >= 0.75 => Pressure.Warning,
        _ => Pressure.Fine,
    };

    private static Gauge? Bar(string label, double? fraction, ulong? used, ulong? total)
    {
        if (fraction is not { } full || used is not { } inUse || total is not { } capacity)
            return null;
        return new Gauge(label, full, $"{Bytes(inUse)} / {Bytes(capacity)}", PressureOf(full));
    }
}
