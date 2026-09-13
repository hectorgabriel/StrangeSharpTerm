using System.Text.Json;
using Anthropic;
using StrangeSharpTerm.Assist.Providers;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// The Claude path, driven end to end against a stub that returns a recorded
/// stream and keeps the request.
///
/// The SDK is doing the decoding, so what these check is the translation either
/// side of it: what an <see cref="AssistRequest"/> becomes on the wire, and what
/// the wire becomes as <see cref="AssistEvent"/>s.
/// </summary>
public class ClaudeBackendTests
{
    [Fact]
    public async Task AnAnswerArrivesAsText()
    {
        var (events, _) = await Stream("claude.answer.sse", Question("what is eating the disk?"));

        Said(events).ShouldContain("The journal is 37G of a 49G filesystem.");
        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.EndTurn);
    }

    [Fact]
    public async Task ShellBlocksInTheAnswerAreTheOnesThatGetAButton()
    {
        var (events, _) = await Stream("claude.answer.sse", Question("what is eating the disk?"));

        Fences.ShellBlocks(Said(events)).Single().Staged.ShouldBe("journalctl --vacuum-size=1G");
    }

    [Fact]
    public async Task ReasoningIsAskedForAndArrives()
    {
        var (events, stub) = await Stream("claude.toolcall.sse", Question("why is the disk full?"));

        Thought(events).ShouldBe("The disk is at 98%, so the journal is worth checking first.");

        // Summarised rather than omitted, which is the default on these models:
        // a pane that is reasoning should show that it is rather than looking
        // frozen.
        var body = JsonDocument.Parse(stub.Body!).RootElement;
        body.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("adaptive");
        body.GetProperty("thinking").GetProperty("display").GetString().ShouldBe("summarized");
    }

    [Fact]
    public async Task AToolCallIsAssembledFromPartialJsonAndEmittedWhole()
    {
        var (events, _) = await Stream("claude.toolcall.sse", Question("why is the disk full?"));

        var call = events.OfType<AssistEvent.Call>().Single().Tool;
        call.Id.ShouldBe("toolu_1");
        call.Name.ShouldBe(AssistTools.RunCommand);

        var (command, why) = AssistTools.ReadRun(call.Arguments);
        command.ShouldBe("df -h /");
        why.ShouldBe("how full, and which device");
    }

    [Fact]
    public async Task ARefusalIsAStopReasonAndNotAnError()
    {
        var (events, _) = await Stream("claude.refusal.sse", Question("something it will not answer"));

        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.Refusal);
        Said(events).ShouldBe("I can't help with that.");
    }

    /// <summary>The system prompt is a field of its own here, not the first message.</summary>
    [Fact]
    public async Task TheSystemPromptIsATopLevelField()
    {
        var (_, stub) = await Stream("claude.answer.sse", Question("why is it slow?"));

        var body = JsonDocument.Parse(stub.Body!).RootElement;
        body.GetProperty("system").ToString().ShouldContain("systems assistant");

        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        messages.Length.ShouldBe(1);
        messages[0].GetProperty("role").GetString().ShouldBe("user");
    }

    [Fact]
    public async Task ToolResultsGoBackAsContentBlocksOnAUserMessage()
    {
        var (_, stub) = await Stream("claude.answer.sse", new AssistRequest
        {
            System = AssistPrompts.HostWithCommands,
            Tools = [AssistTools.Runner],
            Messages =
            [
                new AssistMessage { Role = AssistRole.User, Text = "what is eating the disk?" },
                new AssistMessage
                {
                    Role = AssistRole.Assistant,
                    Text = "Checking.",
                    ToolCalls = [new AssistToolCall("toolu_1", AssistTools.RunCommand, """{"command":"df -h /","why":"how full"}""")],
                },
                new AssistMessage
                {
                    Role = AssistRole.User,
                    ToolResults = [new AssistToolResult("toolu_1", "exit status 0\n/dev/sda1 98% /")],
                },
            ],
        });

        var messages = JsonDocument.Parse(stub.Body!).RootElement.GetProperty("messages").EnumerateArray().ToArray();

        var assistant = messages[1].GetProperty("content").EnumerateArray().ToArray();
        assistant[0].GetProperty("type").GetString().ShouldBe("text");
        assistant[1].GetProperty("type").GetString().ShouldBe("tool_use");
        assistant[1].GetProperty("id").GetString().ShouldBe("toolu_1");
        assistant[1].GetProperty("input").GetProperty("command").GetString().ShouldBe("df -h /");

        var result = messages[2].GetProperty("content").EnumerateArray().Single();
        result.GetProperty("type").GetString().ShouldBe("tool_result");
        result.GetProperty("tool_use_id").GetString().ShouldBe("toolu_1");
    }

    [Fact]
    public async Task TheToolCarriesItsSchema()
    {
        var (_, stub) = await Stream("claude.answer.sse", new AssistRequest
        {
            System = AssistPrompts.HostWithCommands,
            Messages = [new AssistMessage { Role = AssistRole.User, Text = "x" }],
            Tools = [AssistTools.Runner],
        });

        var tool = JsonDocument.Parse(stub.Body!).RootElement.GetProperty("tools").EnumerateArray().Single();
        tool.GetProperty("name").GetString().ShouldBe(AssistTools.RunCommand);

        var schema = tool.GetProperty("input_schema");
        schema.GetProperty("properties").GetProperty("command").GetProperty("type").GetString().ShouldBe("string");
        schema.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .ShouldBe(["command", "why"]);
    }

    [Fact]
    public async Task TheModelAskedForIsTheModelSent()
    {
        var (_, stub) = await Stream("claude.answer.sse", Question("x"), model: "claude-sonnet-5");

        JsonDocument.Parse(stub.Body!).RootElement.GetProperty("model").GetString().ShouldBe("claude-sonnet-5");
    }

    private static AssistRequest Question(string text) => new()
    {
        System = AssistPrompts.Host,
        Messages = [new AssistMessage { Role = AssistRole.User, Text = text }],
    };

    private static async Task<(List<AssistEvent> Events, StubEndpoint Stub)> Stream(
        string fixture,
        AssistRequest request,
        string model = "claude-opus-5")
    {
        var stub = new StubEndpoint(Recorded.Text(fixture));
        var backend = new ClaudeBackend(
            new AnthropicClient { ApiKey = "not-a-real-key", HttpClient = stub.Client() },
            model);

        var events = new List<AssistEvent>();
        await foreach (var streamed in backend.Stream(request, TestContext.Current.CancellationToken))
            events.Add(streamed);

        return (events, stub);
    }

    private static string Said(IEnumerable<AssistEvent> events) =>
        string.Concat(events.OfType<AssistEvent.Say>().Select(say => say.Text));

    private static string Thought(IEnumerable<AssistEvent> events) =>
        string.Concat(events.OfType<AssistEvent.Reasoning>().Select(reasoning => reasoning.Text));
}
