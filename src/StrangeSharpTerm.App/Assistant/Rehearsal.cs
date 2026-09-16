using Avalonia.Controls;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.App.Terminal;
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

    /// <summary>
    /// The whole window, with four sessions tiled and the dock beside them.
    ///
    /// The layout nobody could otherwise look at: it needs four servers and an
    /// API key at once, and the panes it arranges are the ones that already
    /// needed a fixture to be seen at all.
    ///
    /// The terminals are stand-ins rather than the real control. A rehearsed
    /// channel carries nothing, and a terminal reading a stream that is already
    /// at its end asks again forever — so the demo would spin rather than draw.
    /// </summary>
    internal static Window TiledWindow()
    {
        var hosts = new[]
        {
            ("web-01", "web-01.example.com"),
            ("web-02", "web-02.example.com"),
            ("db-primary", "db-01.example.com"),
            ("bastion", "bastion.example.com"),
        };
        var connections = hosts.Select(host => new Connection { Name = host.Item1, Hostname = host.Item2 }).ToArray();

        var settings = new AssistSettings();
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: connections)),
            new RehearsedSessions(),
            (_, _) => Screen(),
            assist: settings,
            backends: _ => new RehearsedBackend(RehearsedBackend.Says(
                "Nothing in this window reached a server: it is a rehearsal of the layout.")),
            assistKeys: Keys());

        var window = new Views.ShellWindow(shell) { Width = 1280, Height = 820 };

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            foreach (var connection in connections)
                await shell.OpenTerminal(connection);

            shell.ToggleTilesCommand.Execute(null);
            shell.Inventory.Selection = connections[0].Id;
            shell.OpenAssistantCommand.Execute(null);
        }, Avalonia.Threading.DispatcherPriority.Background);

        return window;
    }

    /// <summary>
    /// What a session looks like when there is no session: a prompt and the last
    /// thing someone typed at it, one screen per host in the order they open.
    /// </summary>
    private static Control Screen()
    {
        var text = Screens[_screen++ % Screens.Length];
        return new Border
        {
            Background = Avalonia.Application.Current?.FindResource("BackgroundBrush") as Avalonia.Media.IBrush,
            Padding = new Avalonia.Thickness(10),
            Child = new TextBlock
            {
                FontFamily = "monospace",
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = text,
            },
        };
    }

    private static int _screen;

    private static readonly string[] Screens =
    [
        "deploy@web-01:~$ df -h /\nFilesystem      Size  Used Avail Use% Mounted on\n/dev/sda1        49G   "
            + "46G  1.2G  98% /\ndeploy@web-01:~$ ",
        "deploy@web-02:~$ uptime\n 14:22:01 up 12 days,  3:41,  1 user,  load average: 1.85, 1.98, 2.11\n"
            + "deploy@web-02:~$ ",
        "postgres@db-primary:~$ pg_isready\n/var/run/postgresql:5432 - accepting connections\n"
            + "postgres@db-primary:~$ ",
        "deploy@bastion:~$ who\ndeploy   pts/0        14:02 (10.0.0.4)\ndeploy@bastion:~$ ",
    ];

    /// <summary>Every host answering from nowhere, so a whole window can be arranged without a network.</summary>
    private sealed class RehearsedSessions : IHostSessions
    {
        public TerminalSession Shell(Connection connection) => new(new QuietChannel());

        public IRemoteFiles Files(Connection connection) => throw new NotSupportedException("rehearsed");

        public ITunnels Tunnels(Connection connection) => throw new NotSupportedException("rehearsed");

        public IServerHealth Health(Connection connection) => new Invented();

        public IRemoteCommands Commands(Connection connection) => new Invented();

        public bool IsOpen(Connection connection) => true;

        /// <summary>The same made-up server behind both: no request leaves the process.</summary>
        private sealed class Invented : IServerHealth, IRemoteCommands
        {
            public ServerMetrics Collect() => Metrics;

            public CommandResult Run(string command, TimeSpan timeout) =>
                new(0, "Nothing ran: this window is a rehearsal.", "");
        }

        /// <summary>
        /// A channel nothing ever arrives on.
        ///
        /// Not an empty MemoryStream: reading one returns zero, which a terminal
        /// reads as the end of the shell and answers by asking again, forever.
        /// </summary>
        private sealed class QuietChannel : ITerminalChannel
        {
            public Stream Stream { get; } = new Silence();

            public void Resize(int columns, int rows) { }

            public void Dispose() => Stream.Dispose();

            private sealed class Silence : Stream
            {
                public override bool CanRead => true;

                public override bool CanSeek => false;

                public override bool CanWrite => true;

                public override long Length => 0;

                public override long Position { get => 0; set { } }

                public override void Flush() { }

                public override int Read(byte[] buffer, int offset, int count) => Block();

                public override long Seek(long offset, SeekOrigin origin) => 0;

                public override void SetLength(long value) { }

                public override void Write(byte[] buffer, int offset, int count) { }

                private static int Block()
                {
                    Thread.Sleep(Timeout.Infinite);
                    return 0;
                }
            }
        }
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
