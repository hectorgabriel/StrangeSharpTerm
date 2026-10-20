using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// What a pane has spent.
///
/// Nothing counted it before: a conversation resends everything on every turn
/// and nobody could see what that cost, or whether the cache was taking any of
/// it.
/// </summary>
public class UsageTests
{
    private static IReadOnlyList<AssistEvent> Costing(AssistUsage usage, IReadOnlyList<AssistEvent> turn) =>
        [.. turn.Take(turn.Count - 1), new AssistEvent.Spent(usage), turn[^1]];

    /// <summary>
    /// Every request is counted, a command's round trip included: a question
    /// that ran one command is two requests, and the second is the one that
    /// resent the first.
    /// </summary>
    [Fact]
    public async Task EveryRequestAQuestionMakesIsCounted()
    {
        var backend = new ScriptedBackend(
            Costing(new AssistUsage(Input: 500, CacheWrite: 300, Output: 40), ScriptedBackend.Runs("df -h /")),
            Costing(new AssistUsage(Input: 90, CacheRead: 800, Output: 60), ScriptedBackend.Says("It is full.")));
        var meter = new UsageMeter();
        var agent = new HostAgent(
            new MeteredBackend(backend, meter), new FakeHost("web-01"), Fixtures.Settings, new RecordingGate());

        await agent.Ask("is it full?", new AskOptions { MayRunCommands = true }, TestContext.Current.CancellationToken);

        meter.Total.ShouldBe(new AssistUsage(Input: 590, CacheRead: 800, CacheWrite: 300, Output: 100));
        meter.Total.Sent.ShouldBe(1690);
    }

    /// <summary>
    /// One meter for a pane, however many agents it drives -- the orchestrator
    /// has a fleet assistant and a conversation per host in a plan.
    /// </summary>
    [Fact]
    public async Task AgentsSharingAMeterAddUp()
    {
        var meter = new UsageMeter();
        var reported = new List<AssistUsage>();
        meter.Changed += (_, total) => reported.Add(total);

        foreach (var alias in new[] { "web-01", "web-02" })
        {
            var backend = new ScriptedBackend(Costing(new AssistUsage(Input: 100, Output: 10), ScriptedBackend.Says("Fine.")));
            var agent = new HostAgent(
                new MeteredBackend(backend, meter), new FakeHost(alias), Fixtures.Settings, new RecordingGate());
            await agent.Ask("well?", null, TestContext.Current.CancellationToken);
        }

        meter.Total.ShouldBe(new AssistUsage(Input: 200, Output: 20));
        reported.ShouldBe([new AssistUsage(Input: 100, Output: 10), new AssistUsage(Input: 200, Output: 20)]);
    }

    /// <summary>The events still reach the agent: the meter only looks.</summary>
    [Fact]
    public async Task TheMeterPassesEverythingThrough()
    {
        var turn = Costing(new AssistUsage(Input: 1), ScriptedBackend.Says("Hello."));
        var metered = new MeteredBackend(new ScriptedBackend(turn), new UsageMeter());

        var events = new List<AssistEvent>();
        await foreach (var streamed in metered.Stream(
            new AssistRequest { System = "", Messages = [] }, TestContext.Current.CancellationToken))
        {
            events.Add(streamed);
        }

        events.ShouldBe(turn);
    }

    /// <summary>Forgetting a conversation does not unspend it.</summary>
    [Fact]
    public async Task ClearingAConversationKeepsWhatItCost()
    {
        var meter = new UsageMeter();
        var agent = new HostAgent(
            new MeteredBackend(new ScriptedBackend(Costing(new AssistUsage(Input: 100), ScriptedBackend.Says("Fine."))), meter),
            new FakeHost("web-01"),
            Fixtures.Settings,
            new RecordingGate());

        await agent.Ask("well?", null, TestContext.Current.CancellationToken);
        agent.Clear();

        meter.Total.Input.ShouldBe(100);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "")]
    [InlineData(400, 0, 0, 30, "400 in · 0% cached · 30 out")]
    [InlineData(2_000, 8_000, 0, 1_250, "10k in · 80% cached · 1.3k out")]
    [InlineData(100_000, 1_134_000, 0, 9_000, "1.23M in · 91% cached · 9k out")]
    public void WhatItCostIsSaidShortly(long input, long read, long write, long output, string said)
    {
        new AssistUsage(input, read, write, output).Describe().ShouldBe(said);
    }
}
