using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The approval gate with more than one host at it.
///
/// A fan-out runs three hosts at once, so three of them can want a command
/// approved at the same moment. The pane used to hold one pending answer and one
/// caption: the second host to arrive overwrote both, the first was left waiting
/// on an answer nobody could now give, and the run hung with nothing on screen
/// asking anything. Worse, the caption and the pending answer were set
/// independently, so the banner could name one host while the buttons answered
/// for another — approving a restart on a server you were not looking at.
///
/// Here rather than in the plain view-model tests because what is asserted is
/// posted to the UI thread, and only a real dispatcher settles that the same way
/// twice. The same reason <c>WhatTheHostsReportedComesBackToTheNextPlan</c>
/// moved.
/// </summary>
[Collection("window")]
public class ApprovalQueueTests
{
    private static PendingCommand Restart(string host) =>
        new(host, "systemctl restart nginx", "it is wedged", "It restarts a service.", IsDestructive: true);

    private static OrchestratorViewModel Pane() => new(new Nothing(), [], _ => null);

    [Fact]
    public void EveryWaitingHostIsAnsweredInTurnAndNoneIsLost()
    {
        Headless.Run(() =>
        {
            var pane = Pane();

            var answers = new Dictionary<string, Task<bool>>
            {
                ["web-01"] = pane.Allow(Restart("web-01")),
                ["web-02"] = pane.Allow(Restart("web-02")),
                ["db-primary"] = pane.Allow(Restart("db-primary")),
            };

            // One at a time, and it says how many are behind it.
            pane.Waiting.ShouldNotBeNull();
            pane.WaitingMore.ShouldBe("2 more waiting");

            // Answer whoever is shown, as a person would, and check the answer
            // lands on the host whose name is above the buttons.
            var allowed = pane.Waiting!.Host;
            pane.AllowCommand.Execute(null);
            Headless.Finish(answers[allowed]);
            answers[allowed].Result.ShouldBeTrue();

            var refused = pane.Waiting.ShouldNotBeNull().Host;
            refused.ShouldNotBe(allowed);
            pane.WaitingMore.ShouldBe("1 more waiting");
            pane.RefuseCommand.Execute(null);
            Headless.Finish(answers[refused]);
            answers[refused].Result.ShouldBeFalse();

            var last = pane.Waiting.ShouldNotBeNull().Host;
            pane.WaitingMore.ShouldBe("");
            pane.AllowCommand.Execute(null);
            Headless.Finish(answers[last]);
            answers[last].Result.ShouldBeTrue();

            // Nothing left over, and nothing still claiming to be waiting.
            pane.Waiting.ShouldBeNull();
            answers.Values.ShouldAllBe(answer => answer.IsCompleted);
        });
    }

    /// <summary>
    /// Stopping the run answers every host that was waiting, not just the one on
    /// screen. Any left behind would hold their place in the run for ever.
    /// </summary>
    [Fact]
    public void StoppingRefusesEveryWaitingHostAtOnce()
    {
        Headless.Run(() =>
        {
            var pane = Pane();
            var waiting = new[] { "web-01", "web-02", "db-primary" }
                .Select(host => pane.Allow(Restart(host)))
                .ToArray();

            pane.StopCommand.Execute(null);

            foreach (var answer in waiting)
            {
                Headless.Finish(answer);
                answer.Result.ShouldBeFalse();
            }

            pane.Waiting.ShouldBeNull();
        });
    }

    /// <summary>
    /// Closing the pane is the same promise: a run may be waiting on an approval
    /// nobody will now see, and every one of them has to be answered.
    /// </summary>
    [Fact]
    public void ClosingThePaneAnswersEveryoneToo()
    {
        Headless.Run(() =>
        {
            var pane = Pane();
            var waiting = new[] { "web-01", "web-02" }.Select(host => pane.Allow(Restart(host))).ToArray();

            pane.Dispose();

            foreach (var answer in waiting)
            {
                Headless.Finish(answer);
                answer.Result.ShouldBeFalse();
            }
        });
    }

    /// <summary>
    /// A connected tool's call queues beside a command: they come from the same
    /// hosts, at the same moment, through the same gate.
    /// </summary>
    [Fact]
    public void CommandsAndToolCallsShareTheOneQueue()
    {
        Headless.Run(() =>
        {
            var pane = Pane();

            var command = pane.Allow(Restart("web-01"));
            var tool = pane.Allow(new PendingToolCall(
                Host: "web-02",
                Server: "Grafana",
                Tool: "create_incident",
                Destination: "metrics.example.com",
                Arguments: """{"title":"disk"}""",
                ReadOnlyClaim: false,
                MayBeGranted: true));

            // The command arrived first, so it is asked about first, and the
            // tool banner stays empty until its turn.
            pane.Waiting.ShouldNotBeNull().Host.ShouldBe("web-01");
            pane.WaitingTool.ShouldBeNull();

            pane.AllowCommand.Execute(null);
            Headless.Finish(command);
            command.Result.ShouldBeTrue();

            pane.Waiting.ShouldBeNull();
            pane.WaitingTool.ShouldNotBeNull().Tool.ShouldBe("create_incident");

            pane.AllowCommand.Execute(null);
            Headless.Finish(tool);
            tool.Result.ShouldBe(ToolApproval.Once);
        });
    }

    /// <summary>
    /// The whole thing, end to end: a real fan-out where two hosts each want a
    /// command the policy will not run unattended.
    ///
    /// This is the symptom as it appeared — the run simply never finished, and
    /// the banner had gone, so there was nothing on screen to answer and nothing
    /// to say why. It is worth a test of its own because the gate being correct
    /// in isolation is not the same claim as a run that completes.
    /// </summary>
    [Fact]
    public void ARunWhereTwoHostsBothNeedApprovalFinishes()
    {
        Headless.Run(() =>
        {
            var pane = Pane();
            var agents = new[] { "web-01", "web-02" }
                .ToDictionary(
                    alias => alias,
                    alias => new HostAgent(new Restarts(), new Nowhere(alias), new AssistSettings(), pane));

            var run = new Orchestrator(new Nothing()).Ask(
                "restart nginx",
                [.. agents.Select(pair => new OrchestratorTarget(pair.Key, () => pair.Value))],
                mayRunCommands: true,
                CancellationToken.None);

            // Both hosts reach the gate; they are answered one at a time.
            for (var answered = 0; answered < agents.Count; answered++)
            {
                PumpUntil(() => pane.Waiting is not null);
                pane.AllowCommand.Execute(null);
            }

            Headless.Finish(run);

            run.Result.Findings.Count.ShouldBe(2);
            run.Result.Findings.ShouldAllBe(finding => finding.Outcome == HostOutcome.Reported);
            run.Result.CommandsRun.ShouldBe(2);
        });
    }

    /// <summary>Pumps the dispatcher until something the run does on another thread lands.</summary>
    private static void PumpUntil(Func<bool> ready, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready())
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"nothing was waiting at the gate within {seconds}s");
            Thread.Sleep(1);
        }
    }

    /// <summary>Asks to restart nginx, which the policy will not run unattended, then reports.</summary>
    private sealed class Restarts : IAssistBackend
    {
        private int _turn;

        public string ProviderName => "Restarts";

        public string Model => "restarts-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var mine = Interlocked.Increment(ref _turn);
            await Task.Yield();
            if (mine == 1)
            {
                yield return new AssistEvent.Call(new AssistToolCall(
                    "call_1",
                    AssistTools.RunCommand,
                    System.Text.Json.JsonSerializer.Serialize(
                        new { command = "systemctl restart nginx", why = "it is wedged" })));
                yield return new AssistEvent.Finished(AssistStop.ToolUse);
                yield break;
            }

            yield return new AssistEvent.Say("Restarted.");
            yield return new AssistEvent.Finished(AssistStop.EndTurn);
        }
    }

    /// <summary>A host with no server behind it.</summary>
    private sealed class Nowhere(string alias) : IHostAccess
    {
        public string Alias => alias;

        public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostSnapshot());

        public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandOutcome(0, "done"));
    }

    /// <summary>A provider these tests never actually ask anything.</summary>
    private sealed class Nothing : IAssistBackend
    {
        public string ProviderName => "Nothing";

        public string Model => "nothing-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AssistEvent.Finished(AssistStop.EndTurn);
        }
    }
}
