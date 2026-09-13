using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class OrchestratorTests
{
    [Fact]
    public async Task EveryTickedHostReportsAndOneAnswerCollatesThem()
    {
        var collator = new ScriptedBackend(ScriptedBackend.Says("Two of the three have an uncapped journal."));
        var run = await new Orchestrator(collator).Ask(
            "is the journal filling the disk anywhere?",
            [Target("web-01", "Affected."), Target("web-02", "Affected, less so."), Target("db-primary", "Not affected.")],
            mayRunCommands: false,
            TestContext.Current.CancellationToken);

        run.Findings.Select(finding => finding.Alias).ShouldBe(["web-01", "web-02", "db-primary"]);
        run.Findings.ShouldAllBe(finding => finding.Outcome == HostOutcome.Reported);
        run.Collated.ShouldBe("Two of the three have an uncapped journal.");
    }

    [Fact]
    public async Task OnlyConnectedHostsAreAskedAndTheRestSaySo()
    {
        var collator = new ScriptedBackend(ScriptedBackend.Says("Summary."));
        var run = await new Orchestrator(collator).Ask(
            "check the journal",
            [Target("web-01", "Affected."), Target("bastion", "", connected: false)],
            mayRunCommands: false,
            TestContext.Current.CancellationToken);

        var bastion = run.Findings.Single(finding => finding.Alias == "bastion");
        bastion.Outcome.ShouldBe(HostOutcome.NotAsked);
        bastion.Text.ShouldBe("Not connected — connect it and run again.");
        bastion.Label.ShouldBe("not asked");
        run.HostsAsked.ShouldBe(1);
    }

    /// <summary>
    /// A summary that read as though it covered every host when some were
    /// unreachable is the failure this layout exists to prevent, and the
    /// summariser is told the same thing the layout says.
    /// </summary>
    [Fact]
    public async Task TheSummariserIsToldWhichHostsDidNotReport()
    {
        var collator = new ScriptedBackend(ScriptedBackend.Says("Summary."));
        await new Orchestrator(collator).Ask(
            "check the journal",
            [Target("web-01", "Affected."), Target("bastion", "", connected: false)],
            mayRunCommands: false,
            TestContext.Current.CancellationToken);

        var sent = collator.Requests.Single();
        sent.System.ShouldBe(AssistPrompts.Collator);
        sent.System.ShouldContain("no access to any server");
        sent.Messages.Single().Text!.ShouldContain("## bastion (not asked)");
        sent.Messages.Single().Text!.ShouldContain("## web-01 (reported)");
        // The same string on either operating system, as the context block is.
        sent.Messages.Single().Text!.ShouldNotContain("\r");
    }

    [Fact]
    public async Task ItRunsNothingItself()
    {
        // Each target gets its own agent, with the same gate and the same
        // budget. Delegating rather than adding a host argument to the tool is
        // the whole design.
        var collator = new ScriptedBackend(ScriptedBackend.Says("Summary."));
        collator.Requests.Clear();

        await new Orchestrator(collator).Ask(
            "look", [Target("web-01", "Done.")], mayRunCommands: true, TestContext.Current.CancellationToken);

        collator.Requests.Single().Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoMoreThanThreeHostsAreInFlightAtOnce()
    {
        var inFlight = 0;
        var highWater = 0;
        var gate = new object();

        var targets = Enumerable.Range(1, 8).Select(number =>
        {
            var alias = $"host-{number}";
            return new OrchestratorTarget(alias, true, () =>
            {
                var host = new FakeHost(alias);
                var backend = new WatchingBackend(() =>
                {
                    lock (gate)
                        highWater = Math.Max(highWater, ++inFlight);
                }, () =>
                {
                    lock (gate)
                        inFlight--;
                });
                return new HostAgent(backend, host, Fixtures.Settings, new StandingAnswer(true));
            });
        }).ToArray();

        await new Orchestrator(new ScriptedBackend(ScriptedBackend.Says("Summary."))).Ask(
            "look", targets, mayRunCommands: false, TestContext.Current.CancellationToken);

        highWater.ShouldBe(AssistLimits.Concurrency);
    }

    [Fact]
    public async Task SixtyCommandsForTheWholeRunOnTopOfEachHostsOwnTwelve()
    {
        // Six hosts, each of which would happily spend its own twelve. The run
        // budget is what stops the sixty-first.
        var hosts = Enumerable.Range(1, 6).Select(number => $"host-{number}").ToArray();
        // Three hosts are in flight at once, so the record of them has to survive that.
        var ran = new System.Collections.Concurrent.ConcurrentDictionary<string, FakeHost>();

        var targets = hosts.Select(alias => new OrchestratorTarget(alias, true, () =>
        {
            var host = new FakeHost(alias);
            ran[alias] = host;
            var turns = Enumerable.Range(0, 13)
                .Select(number => ScriptedBackend.Runs("uptime", "again", $"c{number}"))
                .Append(ScriptedBackend.Says("Done."))
                .ToArray();
            return new HostAgent(new ScriptedBackend(turns), host, Fixtures.Settings, new StandingAnswer(true));
        })).ToArray();

        var run = await new Orchestrator(new ScriptedBackend(ScriptedBackend.Says("Summary."))).Ask(
            "look", targets, mayRunCommands: true, TestContext.Current.CancellationToken);

        run.CommandsRun.ShouldBe(AssistLimits.RunBudget);
        ran.Values.Sum(host => host.Ran.Count).ShouldBe(AssistLimits.RunBudget);
        // No host exceeded its own twelve either.
        ran.Values.ShouldAllBe(host => host.Ran.Count <= AssistLimits.CommandBudget);
    }

    [Fact]
    public async Task AHostThatFailedSaysSoInItsOwnRow()
    {
        var target = new OrchestratorTarget("web-01", true, () => new HostAgent(
            new ScriptedBackend { Fails = new AssistException("The key was refused.") },
            new FakeHost("web-01"),
            Fixtures.Settings,
            new StandingAnswer(true)));

        var run = await new Orchestrator(new ScriptedBackend(ScriptedBackend.Says("Summary."))).Ask(
            "look", [target], mayRunCommands: false, TestContext.Current.CancellationToken);

        var finding = run.Findings.Single();
        finding.Outcome.ShouldBe(HostOutcome.Failed);
        finding.Text.ShouldBe("The key was refused.");
        finding.Label.ShouldBe("failed");
    }

    [Fact]
    public async Task WhenNoHostReportedThereIsNothingToCollate()
    {
        var collator = new ScriptedBackend(ScriptedBackend.Says("Should not be asked."));

        var run = await new Orchestrator(collator).Ask(
            "look", [Target("bastion", "", connected: false)], mayRunCommands: false, TestContext.Current.CancellationToken);

        run.Collated.ShouldBe("No host reported, so there is nothing to collate.");
        collator.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task LosingTheSummaryIsNotLosingTheRun()
    {
        var collator = new ScriptedBackend { Fails = new AssistException("The provider is down.") };

        var run = await new Orchestrator(collator).Ask(
            "look", [Target("web-01", "Affected.")], mayRunCommands: false, TestContext.Current.CancellationToken);

        run.Findings.Single().Text.ShouldBe("Affected.");
        run.Collated.ShouldContain("The provider is down.");
    }

    [Fact]
    public async Task EachHostIsReportedAsItFinishes()
    {
        var orchestrator = new Orchestrator(new ScriptedBackend(ScriptedBackend.Says("Summary.")));
        var seen = new List<string>();
        orchestrator.Reported += (_, finding) =>
        {
            lock (seen)
                seen.Add(finding.Alias);
        };

        await orchestrator.Ask(
            "look",
            [Target("web-01", "One."), Target("web-02", "Two.")],
            mayRunCommands: false,
            TestContext.Current.CancellationToken);

        seen.ShouldBe(["web-01", "web-02"], ignoreOrder: true);
    }

    private static OrchestratorTarget Target(string alias, string answer, bool connected = true) =>
        new(alias, connected, () => new HostAgent(
            new ScriptedBackend(ScriptedBackend.Says(answer)),
            new FakeHost(alias),
            Fixtures.Settings,
            new StandingAnswer(true)));
}
