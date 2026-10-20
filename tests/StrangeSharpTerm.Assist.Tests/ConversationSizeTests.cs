using System.Text.Json;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// A conversation that stays a size.
///
/// An agent is kept for the life of a pane, and nothing bounded what it had
/// accumulated. A twelve-phase plan is twelve questions on the same host, each
/// able to spend twelve commands returning up to 8,000 characters apiece: about
/// 1.15 million characters by the last phase, resent in full on every turn of
/// it.
/// </summary>
public class ConversationSizeTests
{
    /// <summary>A command whose output is most of a question's whole allowance.</summary>
    private static string Wall() => new('x', AssistLimits.MaxOutputCharacters);

    private static (HostAgent Agent, ScriptedBackend Backend) Talking(int questions)
    {
        // Each question runs one command that comes back with a wall of output,
        // then answers. Enough questions and the conversation outgrows the
        // budget.
        var turns = new List<IReadOnlyList<AssistEvent>>();
        for (var index = 0; index < questions; index++)
        {
            turns.Add(ScriptedBackend.Runs("cat /var/log/syslog", "reading the log", $"call_{index}"));
            turns.Add(ScriptedBackend.Says($"Answer {index}."));
        }

        var backend = new ScriptedBackend([.. turns]);
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, Wall()) };
        return (new HostAgent(backend, host, Fixtures.Settings, new RecordingGate()), backend);
    }

    [Fact]
    public async Task WhatIsSentStopsGrowingOnceItIsLargeEnough()
    {
        // Twenty questions, each worth about 8,000 characters of output, is
        // comfortably past a 160,000 budget.
        var (agent, backend) = Talking(20);

        for (var index = 0; index < 20; index++)
        {
            await agent.Ask(
                $"question {index}?",
                new AskOptions { MayRunCommands = true },
                TestContext.Current.CancellationToken);
        }

        var sent = backend.Requests[^1].Messages.Sum(message => message.Size);
        sent.ShouldBeLessThanOrEqualTo(AssistLimits.MaxConversationCharacters);

        // And the transcript is untouched: it is what the person is reading.
        agent.Entries.Count(entry => entry is TranscriptEntry.Question).ShouldBe(20);
    }

    /// <summary>
    /// The newest question is never the thing that gets cut, and what is sent
    /// still begins at a question rather than half way through an exchange --
    /// a provider refuses a tool result with no call before it.
    /// </summary>
    [Fact]
    public async Task TheNewestQuestionSurvivesAndTheRequestStillStartsAtOne()
    {
        var (agent, backend) = Talking(20);

        for (var index = 0; index < 20; index++)
        {
            await agent.Ask(
                $"question {index}?",
                new AskOptions { MayRunCommands = true },
                TestContext.Current.CancellationToken);
        }

        var sent = backend.Requests[^1].Messages;
        sent[0].Role.ShouldBe(AssistRole.User);
        sent[0].ToolResults.ShouldBeEmpty();

        // The newest question is in there. Not last: by the final turn the
        // conversation ends with the results of what it asked to run.
        sent.ShouldContain(message => message.Text != null && message.Text.Contains("question 19?"));
        sent.ShouldNotContain(message => message.Text != null && message.Text.Contains("question 0?"));
    }

    /// <summary>
    /// Every assistant turn that asked for commands still has their results
    /// after it. Dropping anything smaller than an exchange breaks that, and
    /// the request is refused rather than merely shortened.
    /// </summary>
    [Fact]
    public async Task EveryCallStillHasItsResult()
    {
        var (agent, backend) = Talking(20);

        for (var index = 0; index < 20; index++)
        {
            await agent.Ask(
                $"question {index}?",
                new AskOptions { MayRunCommands = true },
                TestContext.Current.CancellationToken);
        }

        var sent = backend.Requests[^1].Messages;
        for (var index = 0; index < sent.Count; index++)
        {
            if (sent[index].ToolCalls.Count == 0)
                continue;

            var results = sent[index + 1].ToolResults;
            results.Count.ShouldBe(sent[index].ToolCalls.Count);
            results.Select(result => result.CallId)
                .ShouldBe(sent[index].ToolCalls.Select(call => call.Id));
        }
    }

    /// <summary>A short conversation is left exactly as it is.</summary>
    [Fact]
    public async Task NothingIsDroppedFromAConversationThatFits()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says("First."),
            ScriptedBackend.Says("Second."),
            ScriptedBackend.Says("Third."));
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new RecordingGate());

        await agent.Ask("first?", null, TestContext.Current.CancellationToken);
        await agent.Ask("second?", null, TestContext.Current.CancellationToken);
        await agent.Ask("third?", null, TestContext.Current.CancellationToken);

        // Three questions and the two answers between them.
        backend.Requests[^1].Messages.Count.ShouldBe(5);
        agent.Entries.ShouldNotContain(entry => entry is TranscriptEntry.Note);
    }

    /// <summary>
    /// And it says so, once, where the person is reading -- rather than quietly
    /// forgetting what it was told earlier in the same pane.
    /// </summary>
    [Fact]
    public async Task ItSaysWhenItHasLeftSomethingOut()
    {
        var (agent, _) = Talking(20);

        for (var index = 0; index < 20; index++)
        {
            await agent.Ask(
                $"question {index}?",
                new AskOptions { MayRunCommands = true },
                TestContext.Current.CancellationToken);
        }

        var notes = agent.Entries
            .OfType<TranscriptEntry.Note>()
            .Where(note => note.Text.Contains("left out of what was sent"))
            .ToArray();

        notes.ShouldNotBeEmpty();
        // Once per question that had to drop something, never once per turn.
        notes.Length.ShouldBeLessThan(20);
    }

    /// <summary>
    /// The fan-out, which had no bound at all: one run may spend sixty commands,
    /// so a few instructions in the same pane were resending well over a million
    /// characters on every turn.
    /// </summary>
    private static (FleetAgent Agent, IReadOnlyList<FleetHost> Hosts, ScriptedBackend Backend) Fleet(
        int instructions, int commandsEach = 1)
    {
        var turns = new List<IReadOnlyList<AssistEvent>>();
        for (var index = 0; index < instructions; index++)
        {
            for (var command = 0; command < commandsEach; command++)
                turns.Add(FleetRuns("web-01", "cat /var/log/syslog", $"call_{index}_{command}"));
            turns.Add(ScriptedBackend.Says($"Answer {index}."));
        }

        var backend = new ScriptedBackend([.. turns]);
        var host = new FakeHost("web-01") { Answer = _ => new CommandOutcome(0, Wall()) };
        return (new FleetAgent(backend, new RecordingGate()), [new FleetHost("web-01", () => host)], backend);
    }

    private static IReadOnlyList<AssistEvent> FleetRuns(string host, string command, string id) =>
    [
        new AssistEvent.Call(new AssistToolCall(
            id,
            AssistTools.RunCommand,
            JsonSerializer.Serialize(new { host, command, why = "reading the log" }))),
        new AssistEvent.Finished(AssistStop.ToolUse),
    ];

    private static async Task Instruct(FleetAgent agent, IReadOnlyList<FleetHost> hosts, int from, int count)
    {
        for (var index = from; index < from + count; index++)
        {
            await agent.Ask(
                $"instruction {index}?",
                hosts,
                mayRunCommands: true,
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task WhatAFleetSendsStopsGrowingToo()
    {
        var (agent, hosts, backend) = Fleet(20);

        await Instruct(agent, hosts, 0, 20);

        var sent = backend.Requests[^1].Messages;
        sent.Sum(message => message.Size).ShouldBeLessThanOrEqualTo(AssistLimits.MaxConversationCharacters);
        sent.ShouldContain(message => message.Text != null && message.Text.Contains("instruction 19?"));
        sent.ShouldNotContain(message => message.Text != null && message.Text.Contains("instruction 0?"));

        agent.Entries.Count(entry => entry is TranscriptEntry.Question).ShouldBe(20);
    }

    /// <summary>
    /// What a fleet sends still begins at an instruction, and every call in it
    /// still has its result.
    /// </summary>
    [Fact]
    public async Task AFleetDropsWholeExchanges()
    {
        var (agent, hosts, backend) = Fleet(20);

        await Instruct(agent, hosts, 0, 20);

        var sent = backend.Requests[^1].Messages;
        sent[0].Role.ShouldBe(AssistRole.User);
        sent[0].ToolResults.ShouldBeEmpty();
        sent[0].Text.ShouldNotBeNull().ShouldContain("The hosts you can reach:");

        for (var index = 0; index < sent.Count; index++)
        {
            if (sent[index].ToolCalls.Count == 0)
                continue;

            sent[index + 1].ToolResults.Select(result => result.CallId)
                .ShouldBe(sent[index].ToolCalls.Select(call => call.Id));
        }
    }

    /// <summary>
    /// A run that outgrows the bound on its own is sent whole -- cutting into it
    /// would part a call from its result -- but nothing earlier comes with it.
    /// </summary>
    [Fact]
    public async Task ARunLargerThanTheBoundIsSentWholeAndAlone()
    {
        // Twenty-five walls is 200,000 characters in one instruction.
        var (agent, hosts, backend) = Fleet(2, commandsEach: 25);

        await Instruct(agent, hosts, 0, 2);

        var sent = backend.Requests[^1].Messages;
        sent[0].Text.ShouldNotBeNull().ShouldContain("instruction 1?");
        sent.Count(message => message.ToolResults.Count > 0).ShouldBe(25);
    }

    /// <summary>
    /// /clear forgets where the questions began as well, or the next compaction
    /// would remove a range that is not there any more.
    /// </summary>
    [Fact]
    public async Task AFleetCompactsAgainAfterAClear()
    {
        var (agent, hosts, backend) = Fleet(40);

        await Instruct(agent, hosts, 0, 20);
        agent.Clear();
        await Instruct(agent, hosts, 20, 20);

        var sent = backend.Requests[^1].Messages;
        sent.Sum(message => message.Size).ShouldBeLessThanOrEqualTo(AssistLimits.MaxConversationCharacters);
        sent.ShouldNotContain(message => message.Text != null && message.Text.Contains("instruction 19?"));
        sent.ShouldContain(message => message.Text != null && message.Text.Contains("instruction 39?"));
    }

    [Fact]
    public async Task AFleetSaysWhenItHasLeftSomethingOut()
    {
        var (agent, hosts, _) = Fleet(20);

        await Instruct(agent, hosts, 0, 20);

        var notes = agent.Entries
            .OfType<TranscriptEntry.Note>()
            .Where(note => note.Text.Contains("left out of what was sent"))
            .ToArray();

        notes.ShouldNotBeEmpty();
        notes.Length.ShouldBeLessThan(20);
    }

    /// <summary>A short fleet conversation is left exactly as it is.</summary>
    [Fact]
    public async Task AFleetConversationThatFitsKeepsEverything()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says("First."),
            ScriptedBackend.Says("Second."),
            ScriptedBackend.Says("Third."));
        var agent = new FleetAgent(backend, new RecordingGate());
        IReadOnlyList<FleetHost> hosts = [new FleetHost("web-01", () => new FakeHost("web-01"))];

        foreach (var question in new[] { "first?", "second?", "third?" })
            await agent.Ask(question, hosts, mayRunCommands: false, cancellationToken: TestContext.Current.CancellationToken);

        backend.Requests[^1].Messages.Count.ShouldBe(5);
        agent.Entries.ShouldNotContain(entry => entry is TranscriptEntry.Note);
    }
}
