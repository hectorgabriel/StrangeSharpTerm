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
        // As a message of its own -- which ends with the question. The recap
        // at the front still mentions it, and is meant to.
        sent.ShouldNotContain(message => message.Text != null && message.Text.EndsWith("question 0?"));
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
        // As a message of its own -- which ends with the question. The recap
        // at the front still mentions it, and is meant to.
        sent.ShouldNotContain(message => message.Text != null && message.Text.EndsWith("instruction 0?"));

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
        // Not even in the recap: /clear forgets that too.
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

    private static async Task Ask(HostAgent agent, int count)
    {
        for (var index = 0; index < count; index++)
        {
            await agent.Ask(
                $"question {index}?",
                new AskOptions { MayRunCommands = true },
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// What was dropped leaves an account behind, at the front of what is sent:
    /// what was asked, what ran where and how it ended, and what it concluded.
    /// </summary>
    [Fact]
    public async Task WhatWasLeftOutIsRecapped()
    {
        var (agent, backend) = Talking(20);

        await Ask(agent, 20);

        var first = backend.Requests[^1].Messages[0].Text.ShouldNotBeNull();
        first.ShouldStartWith("Earlier in this conversation");
        first.ShouldContain("Asked: question 0?");
        first.ShouldContain("- on web-01: cat /var/log/syslog — exit 0");
        first.ShouldContain("Answered: Answer 0.");

        // And none of the output that made it large.
        first.ShouldNotContain(new string('x', 100));
    }

    /// <summary>
    /// Put on as the request is built, not stored: compacted again, the recap
    /// moves to the new first question rather than piling up in front of it.
    /// </summary>
    [Fact]
    public async Task TheRecapIsSaidOnceHoweverOftenItCompacts()
    {
        var (agent, backend) = Talking(20);

        await Ask(agent, 20);

        var sent = backend.Requests[^1].Messages;
        sent.Count(message => message.Text?.Contains("Earlier in this conversation") == true).ShouldBe(1);

        // Still a question first and an answer after it, which every provider
        // needs: the recap rides in the question rather than before it.
        sent[0].Role.ShouldBe(AssistRole.User);
        sent[1].Role.ShouldBe(AssistRole.Assistant);
    }

    /// <summary>
    /// Bounded, oldest first: twenty dropped exchanges at a few hundred
    /// characters each is fine, a thousand would be the problem again.
    /// </summary>
    [Fact]
    public void TheRecapLetsGoOfItsOldestOnceItIsLarge()
    {
        var recap = new Recap();
        for (var index = 0; index < 200; index++)
        {
            recap.Add([
                new TranscriptEntry.Question($"question {index}? " + new string('q', 250)),
                new TranscriptEntry.Answer { Markdown = $"answer {index}. " + new string('a', 500) },
            ]);
        }

        recap.Size.ShouldBeLessThanOrEqualTo(AssistLimits.MaxRecapCharacters + 120);
        var told = recap.Lead([new AssistMessage { Role = AssistRole.User, Text = "now?" }])[0].Text.ShouldNotBeNull();
        told.ShouldContain("question 199?");
        told.ShouldNotContain("question 0?");
    }

    /// <summary>How each command ended, in words, because "refused" matters as much as "exit 1".</summary>
    [Fact]
    public void EachCommandSaysHowItEnded()
    {
        var told = Recap.Of([
            new TranscriptEntry.Question("restart it?"),
            new TranscriptEntry.Step { Host = "web-01", Command = "systemctl status nginx", Why = "", State = StepState.Ran, ExitStatus = 3 },
            new TranscriptEntry.Step { Host = "web-01", Command = "systemctl restart nginx", Why = "", State = StepState.Refused },
            new TranscriptEntry.Step { Host = "web-02", Command = "journalctl -f", Why = "", State = StepState.TimedOut },
            new TranscriptEntry.Step { Host = "web-02", Command = "tail -f app.log", Why = "", State = StepState.TimedOut, MayStillBeRunning = true },
            new TranscriptEntry.Answer { Markdown = "It is stopped, and you would rather I left it.\nSo I have." },
        ]);

        told.ShouldBe(string.Join('\n',
            "Asked: restart it?",
            "- on web-01: systemctl status nginx — exit 3",
            "- on web-01: systemctl restart nginx — refused by the user",
            "- on web-02: journalctl -f — timed out, stopped",
            "- on web-02: tail -f app.log — timed out, may still be running",
            "Answered: It is stopped, and you would rather I left it. So I have."));
    }

    /// <summary>Nothing has been dropped, so nothing is put in front.</summary>
    [Fact]
    public void AnEmptyRecapChangesNothing()
    {
        IReadOnlyList<AssistMessage> conversation = [new AssistMessage { Role = AssistRole.User, Text = "first?" }];

        new Recap().Lead(conversation).ShouldBe(conversation);
    }

    [Fact]
    public async Task AFleetRecapsWhatItLeftOutToo()
    {
        var (agent, hosts, backend) = Fleet(20);

        await Instruct(agent, hosts, 0, 20);

        var first = backend.Requests[^1].Messages[0].Text.ShouldNotBeNull();
        first.ShouldContain("Asked: instruction 0?");
        first.ShouldContain("- on web-01: cat /var/log/syslog — exit 0");
    }
}
