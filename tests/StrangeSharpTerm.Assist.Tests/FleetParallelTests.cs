using System.Text.Json;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// Reads asked for together run together.
///
/// The same question put to eight hosts used to go one host at a time, each
/// waiting for the slowest server before it, though none of them could change
/// anything.
/// </summary>
public class FleetParallelTests
{
    /// <summary>A host whose commands take a while, and that counts how many are running at once.</summary>
    /// <param name="work">
    /// What a command waits on before it returns. A state rather than a delay
    /// wherever a test depends on it: a delay is a race with a timeout on it,
    /// and a loaded runner is where it loses.
    /// </param>
    private sealed class Slow(string alias, Counter counter, Func<CancellationToken, Task>? work = null) : IHostAccess
    {
        public string Alias => alias;

        public List<string> Ran { get; } = [];

        public Task<HostSnapshot> Look(bool metrics, bool terminalTail, int tailLines, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostSnapshot());

        public async Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            counter.Started(alias);
            try
            {
                await (work?.Invoke(cancellationToken) ?? Task.Delay(30, cancellationToken));
                lock (Ran)
                    Ran.Add(command);
                return new CommandOutcome(0, $"{command} on {alias}");
            }
            finally
            {
                counter.Finished(alias);
            }
        }
    }

    private sealed class Counter
    {
        private int _now;

        private readonly TaskCompletionSource _met = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _othersDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many must be running at once before <see cref="Met"/> completes.</summary>
        public int Meeting { get; init; } = int.MaxValue;

        /// <summary>
        /// Completes once <see cref="Meeting"/> commands are running together.
        /// Bounded, so that commands run one at a time fail the test rather
        /// than hang it.
        /// </summary>
        public Task Met(CancellationToken cancellationToken) => _met.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        /// <summary>Completes once every host but the named one has finished a command.</summary>
        public Task OthersDone(CancellationToken cancellationToken) => _othersDone.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        public int Others { get; init; } = int.MaxValue;

        public int Most { get; private set; }

        public List<string> Order { get; } = [];

        public void Started(string alias)
        {
            lock (Order)
            {
                Order.Add($"start {alias}");
                Most = Math.Max(Most, ++_now);
                if (_now >= Meeting)
                    _met.TrySetResult();
            }
        }

        public void Finished(string alias)
        {
            lock (Order)
            {
                Order.Add($"end {alias}");
                _now--;
                if (Order.Count(entry => entry.StartsWith("end ", StringComparison.Ordinal)) >= Others)
                    _othersDone.TrySetResult();
            }
        }
    }

    private static AssistEvent Call(string host, string command, string id) =>
        new AssistEvent.Call(new AssistToolCall(
            id, AssistTools.RunCommand, JsonSerializer.Serialize(new { host, command, why = "looking" })));

    /// <summary>One turn that asks for all of these at once.</summary>
    private static IReadOnlyList<AssistEvent> Together(params (string Host, string Command)[] calls) =>
        [.. calls.Select((call, index) => Call(call.Host, call.Command, $"c{index}")), new AssistEvent.Finished(AssistStop.ToolUse)];

    private static (FleetAgent Agent, ScriptedBackend Backend, IReadOnlyList<FleetHost> Hosts) Fleet(
        ICommandGate gate, IReadOnlyList<IHostAccess> hosts, params IReadOnlyList<AssistEvent>[] turns)
    {
        var backend = new ScriptedBackend(turns);
        return (new FleetAgent(backend, gate), backend, [.. hosts.Select(host => new FleetHost(host.Alias, () => host))]);
    }

    private static Task Ask(FleetAgent agent, IReadOnlyList<FleetHost> hosts) =>
        agent.Ask("how full is each of them?", hosts, mayRunCommands: true, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReadsAskedForTogetherRunTogether()
    {
        // Each command waits until all three are running: one at a time, the
        // first would wait for the others forever, and the test fails instead.
        var counter = new Counter { Meeting = 3 };
        Slow[] hosts = [.. new[] { "web-01", "web-02", "db-primary" }.Select(alias => new Slow(alias, counter, counter.Met))];
        var (agent, _, reach) = Fleet(
            new RecordingGate(),
            hosts,
            Together(("web-01", "df -h /"), ("web-02", "df -h /"), ("db-primary", "df -h /")),
            ScriptedBackend.Says("All fine."));

        await Ask(agent, reach);

        counter.Most.ShouldBe(3);
        hosts.ShouldAllBe(host => host.Ran.Count == 1);
    }

    /// <summary>No more at once than a run puts in flight anywhere else.</summary>
    [Fact]
    public async Task NeverMoreThanTheRunsConcurrency()
    {
        // The first three wait until three are running, so the cap is reached
        // for certain; the rest find it reached and go straight through.
        var counter = new Counter { Meeting = AssistLimits.Concurrency };
        var hosts = Enumerable.Range(1, 7).Select(index => new Slow($"web-0{index}", counter, counter.Met)).ToArray();
        var (agent, _, reach) = Fleet(
            new RecordingGate(),
            hosts,
            Together([.. hosts.Select(host => (host.Alias, "uptime"))]),
            ScriptedBackend.Says("All up."));

        await Ask(agent, reach);

        counter.Most.ShouldBe(AssistLimits.Concurrency);
        hosts.ShouldAllBe(host => host.Ran.Count == 1);
    }

    /// <summary>
    /// The results go back in the order they were asked for, and the rows
    /// appear in it, whichever host was fastest.
    /// </summary>
    [Fact]
    public async Task ResultsAndRowsKeepTheOrderTheyWereAskedIn()
    {
        // The first host does not answer until both others have.
        var counter = new Counter { Others = 2 };
        Slow[] hosts =
        [
            new("web-01", counter, counter.OthersDone),
            new("web-02", counter),
            new("db-primary", counter),
        ];
        var (agent, backend, reach) = Fleet(
            new RecordingGate(),
            hosts,
            Together(("web-01", "df -h /"), ("web-02", "df -h /"), ("db-primary", "df -h /")),
            ScriptedBackend.Says("All fine."));

        await Ask(agent, reach);

        // The slow one finished last, and still comes first.
        counter.Order[^1].ShouldBe("end web-01");
        var results = backend.Requests[^1].Messages[^1].ToolResults;
        results.Select(result => result.CallId).ShouldBe(["c0", "c1", "c2"]);
        results[0].Output.ShouldStartWith("web-01:");

        agent.Entries.OfType<TranscriptEntry.Step>().Select(step => step.Host)
            .ShouldBe(["web-01", "web-02", "db-primary"]);
    }

    /// <summary>
    /// A command that stops for a person runs alone, in its place: the reads
    /// before it have finished when it is asked about, and the reads after it
    /// wait for the answer -- which may be a refusal of the very thing they
    /// would have looked at.
    /// </summary>
    [Fact]
    public async Task ACommandThatAsksRunsAloneInItsPlace()
    {
        var counter = new Counter();
        Slow[] hosts = [new("web-01", counter), new("web-02", counter), new("db-primary", counter)];
        string[]? whenAsked = null;
        var gate = new RecordingGate
        {
            Answer = _ =>
            {
                lock (counter.Order)
                    whenAsked = [.. counter.Order];
                return true;
            },
        };
        var (agent, _, reach) = Fleet(
            gate,
            hosts,
            Together(
                ("web-01", "df -h /"),
                ("web-02", "df -h /"),
                ("web-01", "systemctl restart nginx"),
                ("db-primary", "df -h /")),
            ScriptedBackend.Says("Restarted."));

        await Ask(agent, reach);

        // Both reads before it had run and finished, and the one after had not started.
        whenAsked.ShouldNotBeNull().ShouldBe(["start web-01", "start web-02", "end web-01", "end web-02"], ignoreOrder: true);
        hosts[2].Ran.ShouldBe(["df -h /"]);
        hosts[0].Ran.ShouldBe(["df -h /", "systemctl restart nginx"]);
    }

    /// <summary>With commands turned off, reads asked for together still reach no host at all.</summary>
    [Fact]
    public async Task WithCommandsOffNothingRunsAtAll()
    {
        var counter = new Counter();
        Slow[] hosts = [new("web-01", counter), new("web-02", counter)];
        var (agent, backend, reach) = Fleet(
            new RecordingGate(),
            hosts,
            Together(("web-01", "df -h /"), ("web-02", "df -h /")),
            ScriptedBackend.Says("I could not look."));

        await agent.Ask("how full?", reach, mayRunCommands: false, cancellationToken: TestContext.Current.CancellationToken);

        counter.Order.ShouldBeEmpty();
        backend.Requests[^1].Messages[^1].ToolResults.ShouldAllBe(result => result.Failed);
    }
}
