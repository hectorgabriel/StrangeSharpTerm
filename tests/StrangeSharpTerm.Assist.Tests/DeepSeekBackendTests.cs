using System.Net;
using System.Text.Json;
using StrangeSharpTerm.Assist.Providers;

namespace StrangeSharpTerm.Assist.Tests;

public class DeepSeekBackendTests
{
    [Fact]
    public async Task TheWholePathRunsAgainstAStubThatRecordsWhatWasSent()
    {
        var stub = new StubEndpoint(Recorded.Text("deepseek.answer.sse"));
        var backend = new DeepSeekBackend(stub.Client(), "deepseek-v4-pro", "https://stub.invalid/chat/completions");

        var said = new List<string>();
        await foreach (var streamed in backend.Stream(Question("is the disk full?"), TestContext.Current.CancellationToken))
        {
            if (streamed is AssistEvent.Say say)
                said.Add(say.Text);
        }

        string.Concat(said).ShouldBe("The journal is 37G of a 49G filesystem.");

        var body = JsonDocument.Parse(stub.Body!).RootElement;
        body.GetProperty("model").GetString().ShouldBe("deepseek-v4-pro");
        body.GetProperty("stream").GetBoolean().ShouldBeTrue();
    }

    /// <summary>The system prompt is the first message here, not a field of its own.</summary>
    [Fact]
    public async Task TheSystemPromptIsTheFirstMessage()
    {
        var body = await Send(Question("why is it slow?"));

        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        messages[0].GetProperty("role").GetString().ShouldBe("system");
        messages[0].GetProperty("content").GetString().ShouldBe(AssistPrompts.Host);
        messages[1].GetProperty("role").GetString().ShouldBe("user");
        messages[1].GetProperty("content").GetString().ShouldBe("why is it slow?");
    }

    /// <summary>
    /// The documented ceiling has moved with every model generation and a value
    /// above it is a 400, while sending none lets the server apply its own.
    /// </summary>
    [Fact]
    public async Task MaxTokensIsNotSent() =>
        (await Send(Question("x"))).TryGetProperty("max_tokens", out _).ShouldBeFalse();

    [Theory]
    [InlineData("deepseek-v4-pro", true)]
    [InlineData("deepseek-v4", true)]
    [InlineData("deepseek-v5-preview", true)]
    [InlineData("deepseek-v3", false)]
    [InlineData("deepseek-chat", false)]
    [InlineData("deepseek-reasoner", false)]
    public void ThinkingIsSentOnlyToTheModelsThatRequireIt(string model, bool expected) =>
        // V4 models require it and earlier ones reject it, so the name is what
        // decides. That is an inference, and the endpoint override is how the
        // day it becomes wrong is survived.
        DeepSeekBackend.Thinks(model).ShouldBe(expected);

    [Fact]
    public async Task ToolResultsAreMessagesOfTheirOwn()
    {
        var request = new AssistRequest
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
                    ToolCalls = [new AssistToolCall("call_1", AssistTools.RunCommand, """{"command":"df -h /","why":"how full"}""")],
                },
                new AssistMessage
                {
                    Role = AssistRole.User,
                    ToolResults = [new AssistToolResult("call_1", "exit status 0\n/dev/sda1 98% /")],
                },
            ],
        };

        var body = await Send(request);
        var messages = body.GetProperty("messages").EnumerateArray().ToArray();

        messages[2].GetProperty("role").GetString().ShouldBe("assistant");
        messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString().ShouldBe("call_1");
        messages[3].GetProperty("role").GetString().ShouldBe("tool");
        messages[3].GetProperty("tool_call_id").GetString().ShouldBe("call_1");
        messages[3].GetProperty("content").GetString().ShouldNotBeNull().ShouldContain("98%");
    }

    [Fact]
    public async Task TheToolIsSentAsAFunction()
    {
        var body = await Send(new AssistRequest
        {
            System = AssistPrompts.HostWithCommands,
            Messages = [new AssistMessage { Role = AssistRole.User, Text = "x" }],
            Tools = [AssistTools.Runner],
        });

        var tool = body.GetProperty("tools")[0];
        tool.GetProperty("type").GetString().ShouldBe("function");
        tool.GetProperty("function").GetProperty("name").GetString().ShouldBe(AssistTools.RunCommand);
        tool.GetProperty("function").GetProperty("parameters").GetProperty("required")
            .EnumerateArray().Select(field => field.GetString()).ShouldBe(["command", "why"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.PaymentRequired, "balance")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limiting")]
    [InlineData(HttpStatusCode.InternalServerError, "server error")]
    public async Task EachFailureSaysSomethingAPersonCanActOn(HttpStatusCode status, string expected)
    {
        var stub = new StubEndpoint("{}", status);
        var backend = new DeepSeekBackend(stub.Client(), "deepseek-v4-pro", "https://stub.invalid/chat/completions");

        var thrown = await Should.ThrowAsync<AssistException>(async () =>
        {
            await foreach (var _ in backend.Stream(Question("x"), TestContext.Current.CancellationToken))
            {
            }
        });

        thrown.Message.ShouldContain(expected);
    }

    private static AssistRequest Question(string text) => new()
    {
        System = AssistPrompts.Host,
        Messages = [new AssistMessage { Role = AssistRole.User, Text = text }],
    };

    private static async Task<JsonElement> Send(AssistRequest request)
    {
        var stub = new StubEndpoint(Recorded.Text("deepseek.answer.sse"));
        var backend = new DeepSeekBackend(stub.Client(), "deepseek-v4-pro", "https://stub.invalid/chat/completions");

        await foreach (var _ in backend.Stream(request, TestContext.Current.CancellationToken))
        {
        }

        return JsonDocument.Parse(stub.Body!).RootElement.Clone();
    }
}
