using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Mcp;
using StrangeSharpTerm.Security;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// The assistant panes over a fixture, for the <c>--demo-*</c> flags.
///
/// See <see cref="RehearsedBackend"/> for why this is in the app rather than in
/// the tests. Everything here is invented: the hosts do not exist, the metrics
/// are made up, and no request leaves the process.
/// </summary>
internal static class Rehearsal
{
    private static readonly ServerMetrics Metrics = new()
    {
        Uptime = "12 days, 3:41",
        LoadAverages = [1.85, 1.98, 2.11],
        MemoryUsedBytes = 6_442_450_944,
        MemoryTotalBytes = 8_589_934_592,
        DiskUsedBytes = 49_392_123_904,
        DiskTotalBytes = 52_613_349_376,
        Containers = [new ServerMetrics.Container("api", "Up 3 hours"), new ServerMetrics.Container("cache", "Up 3 hours")],
    };

    private const string Tail = """
        deploy@web-01:~$ df -h /
        Filesystem      Size  Used Avail Use% Mounted on
        /dev/sda1        49G   46G  1.2G  98% /
        deploy@web-01:~$
        """;

    /// <summary>A store with a key in it, so the sheet shows its configured state. Never the real keychain.</summary>
    internal static ISecretStore Keys()
    {
        var keys = new InMemorySecretStore();
        keys.SetSecret(AssistProvider.Claude.KeyAccount, "not-a-real-key");
        return keys;
    }

    /// <summary>
    /// Two connected servers, over a fixture, connected to nothing.
    ///
    /// The settings section only has anything in it after a person has attached
    /// a server, which is the state nobody would otherwise screenshot.
    /// </summary>
    internal static McpHub Servers()
    {
        var grafana = new McpServerConfig
        {
            Name = "Grafana",
            Transport = McpTransport.Http,
            Url = "https://metrics.example.com/mcp",
            AlwaysAllowed = ["query_range"],
        };
        var runbooks = new McpServerConfig
        {
            Name = "Runbooks",
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "~/runbooks"],
        };

        return new McpHub(new McpSettings { Servers = [grafana, runbooks] });
    }

    /// <summary>The assistant beside a terminal, showing the commands it ran.</summary>
    internal static Window AssistantPane()
    {
        var host = new RehearsedHost("web-01", Metrics, Tail);
        host.Answers["df -h /"] = "Filesystem      Size  Used Avail Use% Mounted on\n/dev/sda1        49G   46G  1.2G  98% /";
        host.Answers["du -xh /var --max-depth=2 | sort -rh | head -10"] =
            "37G\t/var/log/journal\n38G\t/var/log\n39G\t/var\n1.1G\t/var/lib";
        host.Answers["journalctl --disk-usage"] = "Archived and active journals take up 37.0G in the file system.";

        var backend = new RehearsedBackend(
            RehearsedBackend.Runs("df -h /", "how full, and which device", "c1"),
            RehearsedBackend.Runs("du -xh /var --max-depth=2 | sort -rh | head -10", "where the space went", "c2"),
            RehearsedBackend.Runs("journalctl --disk-usage", "confirm the journal", "c3"),
            RehearsedBackend.Says(
                "The journal is the whole of it: 37G of a 49G filesystem, and journald.conf sets no size cap.\n\n"
                + "Reclaim it now, then cap it so it does not come back:\n\n"
                + "```sh\njournalctl --vacuum-size=1G\n```"));

        var settings = new AssistSettings { AllowCommandsByDefault = true };
        AssistantViewModel? model = null;
        model = new AssistantViewModel(
            new HostAgent(backend, host, settings, new Late(() => model!)),
            settings,
            _ => { });

        var window = Window("Assistant — web-01", new AssistantView(model), 640, 720);
        // Asked for it, so there is a conversation to look at rather than an
        // empty pane and a text field.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            await model.LookCommand.ExecuteAsync(null);
            model.Question = "disk is filling up — what is eating it?";
            await model.AskCommand.ExecuteAsync(null);
        }, Avalonia.Threading.DispatcherPriority.Background);

        return window;
    }

    /// <summary>
    /// The orchestrator: finished, or the picker before a run.
    ///
    /// Both, because the empty state is the one that says what the pane is for
    /// and it is the state nobody would otherwise screenshot.
    /// </summary>
    internal static Window OrchestratorPane(bool finished)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["web-01"] = "Affected, and it is the reason the disk is nearly full: /var/log/journal is 37G of a "
                + "49G filesystem and journald.conf sets no size cap.",
            ["web-02"] = "Affected, less severely — journal at 8.1G with the disk 61% full. Same missing cap, so it "
                + "grows without limit.",
            ["db-primary"] = "Not affected. SystemMaxUse=1G is already set and the journal is 940M.",
        };

        var collator = new RehearsedBackend(RehearsedBackend.Says(
            "Two of the three hosts that answered have an uncapped journal, and on one of them it is already the "
            + "reason the disk is nearly full.\n\n"
            + "web-01 is the urgent one — 37G of journal on a 49G disk. web-02 has the same missing cap but only "
            + "8.1G so far. db-primary already sets SystemMaxUse=1G and is fine; that is the setting to copy.\n\n"
            + "bastion was not connected, so nothing here covers it."));

        var settings = new AssistSettings();
        var model = new OrchestratorViewModel(
            collator,
            [
                Target("web-01", true, finished),
                Target("web-02", true, finished),
                Target("db-primary", true, finished),
                Target("bastion", false, finished),
                Target("db-replica", true, false),
                Target("homelab", true, false),
                Target("staging-app", true, false),
                Target("vps-paris", true, false),
            ],
            alias => answers.TryGetValue(alias, out var answer)
                ? new HostAgent(
                    new RehearsedBackend(RehearsedBackend.Says(answer)),
                    new RehearsedHost(alias, Metrics, Tail),
                    settings,
                    new StandingAnswer(true))
                : null);

        var window = Window("Orchestrator", new OrchestratorView(model), 820, 760);

        if (finished)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                model.Instruction = "is the systemd journal filling the disk anywhere?";
                await model.RunCommand.ExecuteAsync(null);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        return window;
    }

    /// <summary>A four-phase cluster plan awaiting approval.</summary>
    internal static Window PlanPane()
    {
        var plan = """
        {"phases": [
          {"name": "Prepare every node", "hosts": ["web-01", "web-02", "staging-app"],
           "task": "Check the OS and whether a container runtime is already present. Disable swap, load the br_netfilter module and set the bridge sysctls, then install containerd, kubeadm, kubelet and kubectl. Verify kubeadm runs and report the versions."},
          {"name": "Initialise the control plane", "hosts": ["web-01"],
           "task": "Run kubeadm init with a pod network CIDR of 10.244.0.0/16, then set up the admin kubeconfig for the current user and install a CNI. Confirm the node reaches Ready before reporting.",
           "capture": "join_command"},
          {"name": "Join the workers", "hosts": ["web-02", "staging-app"],
           "task": "Join this node to the cluster with: {{join_command}} — then confirm the kubelet is running and report what it says."},
          {"name": "Check the cluster", "hosts": ["web-01"],
           "task": "List the nodes and confirm all three are Ready, and that the CNI pods are running. Report anything that is not."}
        ]}
        """;

        var model = new OrchestratorViewModel(
            new RehearsedBackend(RehearsedBackend.Says(plan)),
            [Target("web-01", true, true), Target("web-02", true, true), Target("staging-app", true, true)],
            _ => null);

        var window = Window("Orchestrator — plan", new OrchestratorView(model), 820, 760);

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            model.Mode = OrchestratorMode.Plan;
            model.Instruction = "build a single-control-plane Kubernetes cluster on these three hosts";
            await model.RunCommand.ExecuteAsync(null);
        }, Avalonia.Threading.DispatcherPriority.Background);

        return window;
    }

    private static TargetRow Target(string alias, bool connected, bool chosen) =>
        new() { Alias = alias, IsConnected = connected, IsChosen = chosen };

    private static Window Window(string title, Control pane, int width, int height) =>
        new()
        {
            Title = title,
            Width = width,
            Height = height,
            Content = pane,
            Background = Avalonia.Application.Current?.FindResource("BackgroundBrush") as Avalonia.Media.IBrush,
        };

    /// <summary>The pane is its own gate, and does not exist until the agent it holds does.</summary>
    private sealed class Late(Func<ICommandGate> gate) : ICommandGate
    {
        public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default) =>
            gate().Allow(command, cancellationToken);

        /// <summary>
        /// Forwarded too. A gate that passed on only the command half would let
        /// the interface's default answer -- no -- silently refuse every
        /// connected tool call, which looks exactly like a person refusing.
        /// </summary>
        public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default) =>
            gate().Allow(call, cancellationToken);
    }
}
