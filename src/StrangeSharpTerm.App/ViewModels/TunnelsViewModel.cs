using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// One forward, and whether it is up.
///
/// A row rather than a record, because the state changes under it: a tunnel is
/// the one thing in this app that is running or not running rather than simply
/// being.
/// </summary>
public sealed partial class TunnelRow(PortForward forward) : ObservableObject
{
    public PortForward Forward { get; } = forward;

    public string Name => Forward.Name;

    /// <summary><c>-L 5432:localhost:5432</c>, as ssh would be told it.</summary>
    public string Specification => $"{Forward.SshFlag} {Forward.SshSpecification}";

    /// <summary>What it does, in words, because the flags are not obvious to everyone.</summary>
    public string Explanation => Forward.Kind switch
    {
        PortForwardKind.Local =>
            $"Anything reaching {Where(Forward.BindAddress, Forward.BindPort)} arrives at "
            + $"{Forward.DestinationHost}:{Forward.DestinationPort}, from the server.",
        PortForwardKind.Remote =>
            $"Anything reaching {Where(Forward.BindAddress, Forward.BindPort)} on the server arrives at "
            + $"{Forward.DestinationHost}:{Forward.DestinationPort}, from here.",
        _ => $"A SOCKS proxy on {Where(Forward.BindAddress, Forward.BindPort)}, out through the server.",
    };

    /// <summary>
    /// Ports below 1024 need rights this app does not have and will not ask for.
    /// Said before the attempt rather than after it fails.
    /// </summary>
    public bool NeedsRoot => Forward.RequiresPrivilegedBind;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>Why the last attempt failed, or null.</summary>
    [ObservableProperty]
    public partial string? Failure { get; set; }

    /// <summary>What the button says: the thing it would do, not the state it is in.</summary>
    public string Action => IsRunning ? "Stop" : "Start";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(Action));

    private static string Where(string address, int port) =>
        address.Length == 0 ? $"localhost:{port}" : $"{address}:{port}";
}

/// <summary>
/// A host's tunnels: what it defines, which are up, and the two buttons.
///
/// The forwards themselves come from the inventory with inheritance applied — a
/// folder's forward applies to every host beneath it, which is the one setting
/// that accumulates rather than being replaced.
/// </summary>
public sealed partial class TunnelsViewModel : ObservableObject
{
    private readonly ITunnels _tunnels;
    private readonly Dictionary<NodeId, IRunningTunnel> _running = [];
    private readonly Func<Action, Task> _run;

    public TunnelsViewModel(
        ITunnels tunnels,
        string host,
        IReadOnlyList<PortForward> forwards,
        Func<Action, Task>? offThread = null)
    {
        _tunnels = tunnels;
        Host = host;
        _run = offThread ?? (work => Task.Run(work));
        foreach (var forward in forwards)
            Rows.Add(new TunnelRow(forward));
    }

    public string Host { get; }

    public ObservableCollection<TunnelRow> Rows { get; } = [];

    /// <summary>Nothing to show, which for tunnels is the ordinary case.</summary>
    public bool IsEmpty => Rows.Count == 0;

    public bool AnyRunning => Rows.Any(row => row.IsRunning);

    /// <summary>Starts one, or stops it if it is already up.</summary>
    [RelayCommand]
    public async Task Toggle(TunnelRow? row)
    {
        if (row is null)
            return;
        if (row.IsRunning)
            Stop(row);
        else
            await Start(row);
    }

    /// <summary>Starts everything marked to start with the host.</summary>
    [RelayCommand]
    public async Task StartAutomatic()
    {
        foreach (var row in Rows.Where(row => row.Forward.AutoStart && !row.IsRunning))
            await Start(row);
    }

    [RelayCommand]
    public void StopAll()
    {
        foreach (var row in Rows.Where(row => row.IsRunning))
            Stop(row);
    }

    public async Task Start(TunnelRow row)
    {
        row.Failure = null;
        IRunningTunnel? started = null;

        // Binding a port is not instant and can hang on a server that is gone,
        // so it happens off the UI thread like everything else that talks.
        try
        {
            await _run(() => started = _tunnels.Start(row.Forward));
        }
        catch (Exception e)
        {
            row.Failure = Explain(e, row.Forward);
            row.IsRunning = false;
            OnPropertyChanged(nameof(AnyRunning));
            return;
        }

        _running[row.Forward.Id] = started!;
        row.IsRunning = started!.IsRunning;
        OnPropertyChanged(nameof(AnyRunning));
    }

    /// <summary>
    /// Stops one and lets its port go.
    ///
    /// Synchronous on purpose: stopping is local, and a pane that has to wait to
    /// say a tunnel is down invites a second click that starts it again.
    /// </summary>
    public void Stop(TunnelRow row)
    {
        if (_running.Remove(row.Forward.Id, out var running))
            running.Dispose();
        row.IsRunning = false;
        OnPropertyChanged(nameof(AnyRunning));
    }

    /// <summary>Stops everything this pane started. The pane closing must not leave a port bound.</summary>
    public void Dispose() => StopAll();

    /// <summary>
    /// What to tell the user. The two that happen in practice are a port already
    /// in use and a privileged one, and both are worth naming.
    /// </summary>
    private static string Explain(Exception error, PortForward forward)
    {
        var message = error.Message;
        if (forward.RequiresPrivilegedBind)
            return $"port {forward.BindPort} needs rights this app does not have";
        if (message.Contains("in use", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Address already", StringComparison.OrdinalIgnoreCase))
            return $"something is already listening on {forward.BindPort}";
        return message;
    }
}
