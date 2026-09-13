using StrangeSharpTerm.Assist.Providers;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// The decoder, against streams recorded from the provider. This is how the wire
/// format is checked at all: the documentation for this API was unreachable when
/// the design was written, and two of its details came from a working client.
/// </summary>
public class DeepSeekStreamTests
{
    [Fact]
    public async Task AnAnswerArrivesAsTextAndReasoning()
    {
        var events = await Decode("deepseek.answer.sse");

        Said(events).ShouldBe("The journal is 37G of a 49G filesystem.");
        Thought(events).ShouldBe("The disk is at 98%, so the journal is the first thing to check.");
        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.EndTurn);
    }

    [Fact]
    public async Task AToolCallIsAssembledFromItsPiecesAndEmittedWhole()
    {
        var events = await Decode("deepseek.toolcall.sse");

        var call = events.OfType<AssistEvent.Call>().Single().Tool;
        call.Id.ShouldBe("call_abc");
        call.Name.ShouldBe(AssistTools.RunCommand);

        var (command, why) = AssistTools.ReadRun(call.Arguments);
        command.ShouldBe("df -h /");
        why.ShouldBe("how full, and which device");

        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.ToolUse);
    }

    [Fact]
    public async Task ACallIsEmittedBeforeTheStreamFinishes()
    {
        // The gate has to judge it, so half an argument is no use: the call must
        // arrive complete, and it must arrive before anything says the turn is
        // over.
        var events = await Decode("deepseek.toolcall.sse");

        var call = events.FindIndex(e => e is AssistEvent.Call);
        var finished = events.FindIndex(e => e is AssistEvent.Finished);
        call.ShouldBeLessThan(finished);
    }

    [Fact]
    public async Task AContentFilterIsARefusalAndNotAnError()
    {
        var events = await Decode("deepseek.refusal.sse");

        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.Refusal);
        Said(events).ShouldBe("I cannot help with that.");
    }

    [Fact]
    public async Task AnErrorMidStreamReachesThePaneAsASentence()
    {
        // This API sends 200 and then says it has no balance, which is the only
        // way it reports one.
        var thrown = await Should.ThrowAsync<AssistException>(async () => await Decode("deepseek.error.sse"));

        thrown.Message.ShouldContain("Insufficient Balance");
        thrown.Message.ShouldContain("DeepSeek");
    }

    [Fact]
    public void AChunkThatIsNotJsonIsIgnoredRatherThanFatal() =>
        new DeepSeekStream().Decode("not json at all").ShouldBeEmpty();

    [Fact]
    public void AStreamThatDroppedIsStillFinished()
    {
        var decoder = new DeepSeekStream();
        decoder.Decode("""{"choices":[{"index":0,"delta":{"content":"half an "}}]}""").ToList();

        // No [DONE] ever came. What arrived is still an answer.
        decoder.Finish().OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.EndTurn);
    }

    [Fact]
    public void ChunksWithNoChoicesAreSkipped() =>
        new DeepSeekStream().Decode("""{"id":"x","object":"chat.completion.chunk","choices":[]}""").ShouldBeEmpty();

    private static async Task<List<AssistEvent>> Decode(string fixture)
    {
        var decoder = new DeepSeekStream();
        var events = new List<AssistEvent>();
        await foreach (var payload in Sse.Read(Recorded.Stream(fixture)))
            events.AddRange(decoder.Decode(payload));
        return events;
    }

    private static string Said(IEnumerable<AssistEvent> events) =>
        string.Concat(events.OfType<AssistEvent.Say>().Select(say => say.Text));

    private static string Thought(IEnumerable<AssistEvent> events) =>
        string.Concat(events.OfType<AssistEvent.Reasoning>().Select(reasoning => reasoning.Text));
}
