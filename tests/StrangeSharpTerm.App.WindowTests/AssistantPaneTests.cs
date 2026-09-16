using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The assistant and orchestrator panes, drawn.
///
/// These are here rather than in the plain tests because the agent runs off the
/// UI thread and posts its rows back: a test that watches a row arrive needs
/// something pumping the dispatcher, and <see cref="Headless"/> is it.
/// </summary>
[Collection("window")]
public class AssistantPaneTests
{
    [Fact]
    public void ACommandIsARowInTheSameListAsTheAnswer()
    {
        Headless.Run(() =>
        {
            var host = new Quiet("web-01");
            host.Answers["df -h /"] = "/dev/sda1 49G 46G 1.2G 98% /";

            var model = Pane(
                host,
                Canned.Runs("df -h /", "how full, and which device"),
                Canned.Says("It is 98% full."));
            var window = Show(new AssistantView(model));

            model.MayRunCommands = true;
            model.Question = "what is eating the disk?";
            Headless.Finish(model.AskCommand.ExecuteAsync(null));
            Settle(window);

            // What it actually ran should never require looking somewhere else.
            var shown = Shown(window);
            shown.ShouldContain("what is eating the disk?");
            shown.ShouldContain("df -h /");
            shown.ShouldContain("how full, and which device");
            shown.ShouldContain("It is 98% full.");
            // A read-only command ran without anybody being asked.
            shown.ShouldContain("auto");
        });
    }

    [Fact]
    public void AWritingCommandStopsAndTheRowIsTheQuestion()
    {
        Headless.Run(() =>
        {
            var model = Pane(
                new Quiet("web-01"),
                Canned.Runs("systemctl restart nginx", "restart it"),
                Canned.Says("Done."));
            var window = Show(new AssistantView(model));

            model.MayRunCommands = true;
            model.Question = "restart nginx";
            var asking = model.AskCommand.ExecuteAsync(null);

            // It stops at the gate: the command, its reason, and the two answers
            // are all on screen, and nothing else happens until one is pressed.
            Pump(() => model.Waiting is not null);
            Settle(window);

            var shown = Shown(window);
            shown.ShouldContain("systemctl restart nginx");
            shown.ShouldContain("Run it");
            shown.ShouldContain("No");
            model.Waiting.ShouldNotBeNull().Gate.ShouldNotBeEmpty();

            model.RefuseCommand.Execute(null);
            Headless.Finish(asking);
            Settle(window);

            model.Rows.Last(row => row.IsStep).State.ShouldBe("refused");

            // Drawn, not only computed. The row was put on screen while the
            // command was still waiting, so what this checks is that the one
            // already there changed -- a step is a record whose state changes as
            // it runs, and a row looked up by value is lost the moment it does.
            var after = In<TextBlock>(window).Select(block => block.Text).ToArray();
            after.ShouldContain("refused");
            after.ShouldNotContain("waiting for you");
        });
    }

    [Fact]
    public void FindingsReadInTheOrderTheHostsWereTickedNotTheOrderTheyAnswerIn()
    {
        Headless.Run(() =>
        {
            // web-01 takes its time; bastion is not connected and reports at
            // once. A list that reordered itself as each host finished would
            // come out differently on a second reading.
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("Summary.")),
                [
                    new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true },
                    new TargetRow { Alias = "bastion", IsConnected = false, IsChosen = true },
                    new TargetRow { Alias = "web-02", IsConnected = true, IsChosen = true },
                ],
                alias => alias == "bastion"
                    ? null
                    : new HostAgent(
                        new Slow($"{alias} is affected.", alias == "web-01" ? 80 : 10),
                        new Quiet(alias),
                        new AssistSettings(),
                        new StandingAnswer(true)));
            var window = Show(new OrchestratorView(model));

            model.Instruction = "is the journal filling the disk?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            model.Findings.Select(finding => finding.Alias).ShouldBe(["web-01", "bastion", "web-02"]);
            // And the question they are answers to is above them.
            In<TextBlock>(window).Select(block => block.Text)
                .ShouldContain("is the journal filling the disk?");
        });
    }

    /// <summary>A backend that takes its time, so the hosts finish out of order.</summary>
    private sealed class Slow(string answer, int milliseconds) : IAssistBackend
    {
        public string ProviderName => "Slow";

        public string Model => "slow-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(milliseconds, cancellationToken);
            yield return new AssistEvent.Say(answer);
            yield return new AssistEvent.Finished(AssistStop.EndTurn);
        }
    }

    [Fact]
    public void ASuggestedCommandIsTypedAndNotRun()
    {
        Headless.Run(() =>
        {
            var typed = new List<string>();
            var model = Pane(new Quiet("web-01"), Canned.Says("Try this:\n\n```sh\ndf -h /\n```"), stage: typed.Add);
            var window = Show(new AssistantView(model));

            model.Question = "how full is it?";
            Headless.Finish(model.AskCommand.ExecuteAsync(null));
            Settle(window);

            In<TextBlock>(window).Select(block => block.Text).ShouldContain("Type it");

            model.StageCommand.Execute(model.Rows.Last().Staged.Single());

            // No newline: the person reads it and presses Return.
            typed.ShouldBe(["df -h /"]);
        });
    }

    [Fact]
    public void TheHeaderNamesWhoIsAnswering()
    {
        Headless.Run(() =>
        {
            var model = Pane(new Quiet("web-01"), Canned.Says("."));
            var window = Show(new AssistantView(model));

            var shown = Shown(window);
            shown.ShouldContain("Assistant");
            shown.ShouldContain("web-01");
            shown.ShouldContain("Canned · canned-1");
        });
    }

    [Fact]
    public void TheDisclosureShowsTheRequestItself()
    {
        Headless.Run(() =>
        {
            var host = new Quiet("web-01")
            {
                Snapshot = new HostSnapshot(
                    "Linux 6.1.0 x86_64",
                    new ServerMetrics { Uptime = "3 days" },
                    "deploy@web-01:~$ export API_TOKEN=abcdefghijkl"),
            };
            var model = Pane(host, Canned.Says("."));
            Show(new AssistantView(model));

            Headless.Finish(model.LookCommand.ExecuteAsync(null));

            model.Preview.ShouldContain("Host: web-01");
            model.Preview.ShouldContain("up 3 days");
            model.Preview.ShouldContain("API_TOKEN");
            model.Preview.ShouldNotContain("abcdefghijkl");
            model.RedactionNote.ShouldBe("1 secret removed.");
        });
    }

    [Fact]
    public void EveryHostIsListedAboveTheCollatedAnswerIncludingTheOnesNotAsked()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("Two of the three are affected.")),
                [
                    new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true },
                    new TargetRow { Alias = "web-02", IsConnected = true, IsChosen = true },
                    new TargetRow { Alias = "bastion", IsConnected = false, IsChosen = true },
                ],
                alias => alias == "bastion"
                    ? null
                    : new HostAgent(
                        new Canned(Canned.Says($"{alias} is affected.")),
                        new Quiet(alias),
                        new AssistSettings(),
                        new StandingAnswer(true)));
            var window = Show(new OrchestratorView(model));

            model.Instruction = "is the journal filling the disk?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            var shown = Shown(window);
            shown.ShouldContain("web-01");
            shown.ShouldContain("bastion");
            // A host the run cannot get an agent for is no longer skipped in
            // silence: it gets a row of its own saying what became of it. Not
            // asked rather than failed -- nothing went wrong on that host,
            // there was simply nothing here to ask it with.
            shown.ShouldContain("not asked");
            shown.ShouldContain("reported");
            // A summary that read as though it covered every host is the failure
            // this layout exists to prevent.
            shown.ShouldContain("ACROSS ALL HOSTS");
            shown.ShouldContain("Two of the three are affected.");
        });
    }

    /// <summary>
    /// The reasoning used to be dropped between the provider and the pane. It is
    /// the half that says why one of three identical servers was chosen, which is
    /// the half worth reading -- so it goes on screen, and stays there folded once
    /// the plan it explains has arrived.
    /// </summary>
    [Fact]
    public void ThePlannersReasoningIsShownAndThenFoldedAway()
    {
        Headless.Run(() =>
        {
            const string plan = """
            {"phases":[{"name":"Install OpenClaw","hosts":["web-01"],
              "commands":["apt-get install -y openclaw"]}]}
            """;
            var model = new OrchestratorViewModel(
                new Canned([
                    new AssistEvent.Reasoning("web-01 has the most memory, so OpenClaw goes there."),
                    new AssistEvent.Say(plan),
                    new AssistEvent.Finished(AssistStop.EndTurn),
                ]),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                _ => null);
            var window = Show(new OrchestratorView(model));

            model.Mode = OrchestratorMode.Plan;
            model.Instruction = "where should OpenClaw go?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            model.Thinking.ShouldBe("web-01 has the most memory, so OpenClaw goes there.");
            model.HasThinking.ShouldBeTrue();
            // Folded, because the plan is what there is to read now.
            model.IsThinkingOpen.ShouldBeFalse();
            In<TextBlock>(window).Select(block => block.Text).ShouldContain("THINKING");

            // And a click brings it back rather than having to ask again.
            model.ToggleThinkingCommand.Execute(null);
            Settle(window);
            model.IsThinkingOpen.ShouldBeTrue();
            In<TextBlock>(window).Select(block => block.Text)
                .ShouldContain("web-01 has the most memory, so OpenClaw goes there.");
        });
    }

    /// <summary>
    /// The plan ran, every host reported, and the results stopped at the screen:
    /// the next question was answered by the one participant that never found out
    /// whether any of it worked.
    ///
    /// Here rather than in the view-model tests because it turns on state that is
    /// posted to the UI thread -- whether a plan is on screen decides whether the
    /// button plans or runs -- and only a real dispatcher settles that the same
    /// way twice.
    /// </summary>
    [Fact]
    public void WhatTheHostsReportedComesBackToTheNextPlan()
    {
        Headless.Run(() =>
        {
            const string plan = """
            {"phases":[{"name":"Install OpenClaw","hosts":["web-01"],
              "commands":["apt-get install -y openclaw"]}]}
            """;
            var planner = new Canned(Canned.Says(plan), Canned.Says(plan));
            var model = new OrchestratorViewModel(
                planner,
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                alias => new HostAgent(
                    new Canned(Canned.Says("E: Unable to locate package openclaw")),
                    new Quiet(alias),
                    new AssistSettings(),
                    new StandingAnswer(true)));
            var window = Show(new OrchestratorView(model));

            model.Mode = OrchestratorMode.Plan;
            model.Instruction = "install OpenClaw";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);
            model.Phases.ShouldHaveSingleItem();

            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            // With a plan on screen the button runs it, so planning again starts
            // by throwing the old one away -- which drops the phases, not the
            // memory.
            model.DiscardCommand.Execute(null);
            Settle(window);
            model.Instruction = "try something else";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            var asked = planner.Requests[^1].Messages[^1].Text.ShouldNotBeNull();
            asked.ShouldContain("Unable to locate package openclaw");
            asked.ShouldContain("Install OpenClaw");
            asked.ShouldContain("try something else");
        });
    }

    /// <summary>
    /// What each host said to the model, and what the model said back, under the
    /// row for that host. Until now a run showed only the sentence each host
    /// ended on: the commands it ran and what came back were visible only if you
    /// had an assistant pane open on that host.
    /// </summary>
    [Fact]
    public void EachHostsExchangeIsThereToOpenUnderneathItsRow()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("Both are fine.")),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                alias => new HostAgent(
                    new Canned(
                        Canned.Runs("df -h", "to see the disk"),
                        Canned.Says("Plenty of room.")),
                    new Quiet(alias),
                    new AssistSettings { AllowCommandsByDefault = true },
                    new StandingAnswer(true)));
            var window = Show(new OrchestratorView(model));

            model.Instruction = "is the disk full?";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            var host = model.Findings.ShouldHaveSingleItem();
            host.Alias.ShouldBe("web-01");
            host.HasExchange.ShouldBeTrue();

            // The question it was given, the command it ran, and its answer.
            host.Exchange.Select(row => row.Text).ShouldContain("is the disk full?");
            host.Exchange.ShouldContain(row => row.Command == "df -h");
            host.Exchange.Select(row => row.Text).ShouldContain("Plenty of room.");

            // Folded until asked for, and the command is on screen once it is.
            host.IsExchangeOpen.ShouldBeFalse();
            In<TextBlock>(window).Select(block => block.Text).ShouldNotContain("df -h");

            host.ToggleExchangeCommand.Execute(null);
            Settle(window);
            In<TextBlock>(window).Select(block => block.Text).ShouldContain("df -h");
        });
    }

    /// <summary>
    /// The row is there from the moment the host is asked, not from the moment it
    /// answers: a phase can take minutes, and a list that stays empty until it is
    /// over looks like nothing is happening.
    /// </summary>
    [Fact]
    public void AHostHasARowWhileItIsStillWorking()
    {
        Headless.Run(() =>
        {
            var answering = new TaskCompletionSource();
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("Done.")),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                alias => new HostAgent(
                    new Waiting(answering.Task, "Finished."),
                    new Quiet(alias),
                    new AssistSettings(),
                    new StandingAnswer(true)));
            var window = Show(new OrchestratorView(model));

            model.Instruction = "is the disk full?";
            var run = model.RunCommand.ExecuteAsync(null);
            Settle(window);

            // Asked, not answered.
            var host = model.Findings.ShouldHaveSingleItem();
            host.Alias.ShouldBe("web-01");
            host.Label.ShouldBe("working");
            In<TextBlock>(window).Select(block => block.Text).ShouldContain("web-01");

            answering.SetResult();
            Headless.Finish(run);
            Settle(window);

            host.Label.ShouldBe("reported");
            host.Text.ShouldBe("Finished.");
        });
    }

    [Fact]
    public void APlanIsShownInFullAndNothingRunsUntilItIsRun()
    {
        Headless.Run(() =>
        {
            const string plan = """
            {"phases":[
              {"name":"Prepare every node","hosts":["web-01","web-02"],
               "why":"Both need the runtime before either can join.",
               "commands":["apt-get install -y containerd"]},
              {"name":"Initialise the control plane","hosts":["web-01"],
               "commands":["kubeadm init"],"capture":"join_command"},
              {"name":"Join the workers","hosts":["web-02"],"commands":["{{join_command}}"]}
            ]}
            """;
            var asked = new List<string>();
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says(plan)),
                [
                    new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true },
                    new TargetRow { Alias = "web-02", IsConnected = true, IsChosen = true },
                ],
                alias =>
                {
                    asked.Add(alias);
                    return null;
                });
            var window = Show(new OrchestratorView(model));

            model.Mode = OrchestratorMode.Plan;
            model.Instruction = "build a cluster";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            // Every phase, its hosts, the reason for it, and the commands each
            // host will be given -- as commands, because a plan is worth reading
            // only to the extent the thing read is the thing that runs.
            var shown = Shown(window);
            shown.ShouldContain("Prepare every node");
            shown.ShouldContain("apt-get install -y containerd");
            shown.ShouldContain("Both need the runtime before either can join.");
            shown.ShouldContain("yields join_command");
            shown.ShouldContain("uses join_command");
            shown.ShouldContain("Run the plan");

            // It wrote the plan and ran none of it.
            asked.ShouldBeEmpty();
            model.Phases.Count.ShouldBe(3);
            model.Phases.ShouldAllBe(phase => phase.IsEnabled);
        });
    }

    [Fact]
    public void APlanThatWouldNotBeSafeIsRefusedRatherThanShown()
    {
        Headless.Run(() =>
        {
            var model = new OrchestratorViewModel(
                new Canned(Canned.Says("""{"phases":[{"name":"Do it","hosts":["db-primary"],"commands":["true"]}]}""")),
                [new TargetRow { Alias = "web-01", IsConnected = true, IsChosen = true }],
                _ => null);
            var window = Show(new OrchestratorView(model));

            model.Mode = OrchestratorMode.Plan;
            model.Instruction = "build a cluster";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Settle(window);

            model.Phases.ShouldBeEmpty();
            // The refusal says which phase and why, where a plan would have been.
            model.Refusal.ShouldNotBeNull().ShouldContain("db-primary");
            In<TextBlock>(window).Select(block => block.Text).ShouldContain(model.Refusal);
        });
    }

    [Fact]
    public void AToolCallShowsWhatItIsBeforeAnyoneApprovesIt()
    {
        Headless.Run(() =>
        {
            var tools = new OneTool();
            var settings = new AssistSettings();
            AssistantViewModel? model = null;
            model = new AssistantViewModel(
                new HostAgent(
                    new Canned(
                        Canned.CallsTool("grafana__query_range", """{"query":"up","range":"1h"}"""),
                        Canned.Says("It is up.")),
                    new Quiet("web-01"),
                    settings,
                    new Late(() => model!),
                    tools),
                settings,
                _ => { });

            var window = Show(new AssistantView(model));
            model.MayRunCommands = true;
            model.Question = "is it up?";
            var asking = model.AskCommand.ExecuteAsync(null);

            Pump(() => model.WaitingTool is not null);
            Settle(window);

            // The server, the tool, where it goes and the exact arguments are
            // all on screen: a gate whose substance is one click away is a gate
            // people approve without reading.
            var shown = Shown(window);
            shown.ShouldContain("Grafana");
            shown.ShouldContain("query_range");
            shown.ShouldContain("Its arguments go to metrics.example.com");
            shown.OfType<string>().ShouldContain(text => text.Contains("\"query\": \"up\"", StringComparison.Ordinal));
            shown.ShouldContain("Always allow");

            model.RefuseCommand.Execute(null);
            Headless.Finish(asking);
            Settle(window);

            tools.Called.ShouldBeEmpty();
        });
    }

    [Fact]
    public void ADestructiveToolIsNeverOfferedAStandingPass()
    {
        Headless.Run(() =>
        {
            var tools = new OneTool { Destructive = true };
            var settings = new AssistSettings();
            AssistantViewModel? model = null;
            model = new AssistantViewModel(
                new HostAgent(
                    new Canned(Canned.CallsTool("grafana__query_range", "{}"), Canned.Says("Done.")),
                    new Quiet("web-01"),
                    settings,
                    new Late(() => model!),
                    tools),
                settings,
                _ => { });

            var window = Show(new AssistantView(model));
            model.MayRunCommands = true;
            model.Question = "delete it";
            var asking = model.AskCommand.ExecuteAsync(null);

            Pump(() => model.WaitingTool is not null);
            Settle(window);

            // A tool the server itself calls destructive cannot be given a
            // standing pass, so the bar must not offer one.
            In<TextBlock>(window).Select(block => block.Text).ShouldNotContain("Always allow");

            model.RefuseCommand.Execute(null);
            Headless.Finish(asking);
        });
    }

    /// <summary>One connected tool, reaching no server.</summary>
    private sealed class OneTool : IExternalTools
    {
        internal bool Destructive { get; init; }

        internal List<string> Called { get; } = [];

        public IReadOnlyList<AssistTool> Offered =>
            [new AssistTool("grafana__query_range", "Query a range.", "{}")];

        public bool Owns(string qualifiedName) => qualifiedName == "grafana__query_range";

        public PendingToolCall Describe(string host, string qualifiedName, string argumentsJson) =>
            new(host, "Grafana", "query_range", "metrics.example.com",
                StrangeSharpTerm.Mcp.McpHub.Pretty(argumentsJson), ReadOnlyClaim: false, MayBeGranted: !Destructive);

        public bool MayRunUnattended(string qualifiedName) => false;

        public void Grant(string qualifiedName)
        {
        }

        public Task<ToolReply> Call(string qualifiedName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            Called.Add(qualifiedName);
            return Task.FromResult(new ToolReply("up"));
        }
    }

    private static AssistantViewModel Pane(
        IHostAccess host,
        IReadOnlyList<AssistEvent> first,
        IReadOnlyList<AssistEvent>? second = null,
        Action<string>? stage = null)
    {
        var settings = new AssistSettings();
        var backend = second is null ? new Canned(first) : new Canned(first, second);
        AssistantViewModel? model = null;
        model = new AssistantViewModel(
            new HostAgent(backend, host, settings, new Late(() => model!)),
            settings,
            stage ?? (_ => { }));
        return model;
    }

    private static Window Show(Control pane)
    {
        var window = new Window { Content = pane, Width = 720, Height = 700 };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>Pumps until something becomes true, or fails rather than hanging.</summary>
    private static void Pump(Func<bool> until, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!until())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"it did not happen within {seconds}s");
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    /// <summary>
    /// Every word on screen.
    ///
    /// Inlines as well as Text, because an answer is drawn as runs now -- bold,
    /// inline code, a list marker beside its item -- and its words are no
    /// longer any one TextBlock's Text.
    /// </summary>
    private static string?[] Shown(Visual root) =>
        [.. In<TextBlock>(root).SelectMany(block => new[] { block.Text }
            .Concat(block.Inlines?.OfType<Run>().Select(run => run.Text) ?? []))];

    private sealed class Late(Func<ICommandGate> gate) : ICommandGate
    {
        public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default) =>
            gate().Allow(command, cancellationToken);

        public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default) =>
            gate().Allow(call, cancellationToken);
    }

    private sealed class Quiet(string alias) : IHostAccess
    {
        public string Alias => alias;

        internal HostSnapshot Snapshot { get; set; } = new();

        internal Dictionary<string, string> Answers { get; } = [];

        public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandOutcome(0, Answers.GetValueOrDefault(command, "")));
    }

    /// <summary>A provider that says nothing until the test lets it, so a run can be caught mid-flight.</summary>
    private sealed class Waiting(Task until, string answer) : IAssistBackend
    {
        public string ProviderName => "Waiting";

        public string Model => "waiting-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await until;
            yield return new AssistEvent.Say(answer);
            yield return new AssistEvent.Finished(AssistStop.EndTurn);
        }
    }

    private sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Canned";

        public string Model => "canned-1";

        /// <summary>Every request it was handed, in order.</summary>
        internal List<AssistRequest> Requests { get; } = [];

        internal static IReadOnlyList<AssistEvent> Says(string text) =>
            [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

        internal static IReadOnlyList<AssistEvent> CallsTool(string tool, string arguments) =>
        [
            new AssistEvent.Call(new AssistToolCall("t1", tool, arguments)),
            new AssistEvent.Finished(AssistStop.ToolUse),
        ];

        internal static IReadOnlyList<AssistEvent> Runs(string command, string why) =>
        [
            new AssistEvent.Call(new AssistToolCall(
                "c1",
                AssistTools.RunCommand,
                System.Text.Json.JsonSerializer.Serialize(new { command, why }))),
            new AssistEvent.Finished(AssistStop.ToolUse),
        ];

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var turn = _turn < turns.Length ? turns[_turn] : Says("Nothing more.");
            _turn++;
            foreach (var streamed in turn)
            {
                await Task.Yield();
                yield return streamed;
            }
        }
    }
}
