using System.Net;
using System.Text.Json;
using StrangeSharpTerm.Assist.Providers;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// The Gemini path, driven end to end through Google's SDK against a stub that
/// returns a stream and keeps the request.
///
/// The gemini.* fixtures were written from Google's documented wire format, not
/// recorded: there was no account to record one with. What these check is the
/// translation either side of the SDK, and the thought signature above all,
/// because a signature dropped on the way back is a 400 on Gemini 3 and nothing
/// short of a live request would otherwise say so.
/// </summary>
public class GeminiBackendTests
{
    [Fact]
    public async Task AnAnswerArrivesAsTextAndReasoning()
    {
        var (events, _) = await Stream("gemini.answer.sse", Question("what is eating the disk?"));

        Said(events).ShouldBe("The journal is 37G of a 49G filesystem.");
        Thought(events).ShouldBe("The disk is at 98%, so the journal is the first thing to check.");
        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.EndTurn);
    }

    [Fact]
    public async Task TheRequestGoesToTheModelWithTheKeyInAHeader()
    {
        var (_, stub) = await Stream("gemini.answer.sse", Question("x"));

        var request = stub.Request.ShouldNotBeNull();
        request.RequestUri.ShouldNotBeNull().AbsolutePath.ShouldEndWith("/models/gemini-3.8-flash:streamGenerateContent");
        request.RequestUri.Query.ShouldContain("alt=sse");
        // In a header rather than the URL, where a proxy log would keep it.
        request.Headers.GetValues("x-goog-api-key").Single().ShouldBe("not-a-real-key");
    }

    [Fact]
    public async Task TheSystemPromptIsAFieldAndThoughtsAreAskedFor()
    {
        var (_, stub) = await Stream("gemini.answer.sse", Question("why is it slow?"));

        var body = JsonDocument.Parse(stub.Body!).RootElement;
        body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()
            .ShouldBe(AssistPrompts.Host);
        body.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("includeThoughts")
            .GetBoolean().ShouldBeTrue();

        var contents = body.GetProperty("contents").EnumerateArray().ToArray();
        contents.Length.ShouldBe(1);
        contents[0].GetProperty("role").GetString().ShouldBe("user");
        contents[0].GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("why is it slow?");
    }

    [Fact]
    public async Task ParallelCallsArriveWholeAndTheTurnEndsInToolUse()
    {
        var (events, _) = await Stream("gemini.toolcall.sse", Question("why is the disk full?"));

        var calls = events.OfType<AssistEvent.Call>().Select(call => call.Tool).ToArray();
        calls.Select(call => call.Id).ShouldBe(["fc_df", "fc_du"]);
        calls.ShouldAllBe(call => call.Name == AssistTools.RunCommand);

        var (command, why) = AssistTools.ReadRun(calls[0].Arguments);
        command.ShouldBe("df -h /");
        why.ShouldBe("how full, and which device");

        // Gemini ends a turn of calls with a plain STOP; the loop above needs to
        // hear ToolUse or it never runs them.
        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.ToolUse);
    }

    [Fact]
    public async Task OnlyTheFirstOfParallelCallsCarriesASignature()
    {
        var (events, _) = await Stream("gemini.toolcall.sse", Question("why is the disk full?"));

        var calls = events.OfType<AssistEvent.Call>().Select(call => call.Tool).ToArray();
        calls[0].Signature.ShouldBe("c2lnbmF0dXJlLW9uZQ==");
        calls[1].Signature.ShouldBeNull();
    }

    /// <summary>
    /// The one detail Gemini 3 is strict about: a call goes back with the
    /// signature it came with, on the same part, and its result names the
    /// function it answers.
    /// </summary>
    [Fact]
    public async Task ACallGoesBackWithItsSignatureAndItsResultNamesTheFunction()
    {
        var (_, stub) = await Stream("gemini.answer.sse", Conversation(
        [
            new AssistToolCall("fc_df", AssistTools.RunCommand, """{"command":"df -h /","why":"how full"}""", "c2lnbmF0dXJlLW9uZQ=="),
            new AssistToolCall("fc_du", AssistTools.RunCommand, """{"command":"du -xsh /var","why":"largest"}"""),
        ]));

        var contents = JsonDocument.Parse(stub.Body!).RootElement.GetProperty("contents").EnumerateArray().ToArray();

        contents[1].GetProperty("role").GetString().ShouldBe("model");
        var parts = contents[1].GetProperty("parts").EnumerateArray().ToArray();
        parts[0].GetProperty("text").GetString().ShouldBe("Checking.");
        parts[1].GetProperty("functionCall").GetProperty("id").GetString().ShouldBe("fc_df");
        parts[1].GetProperty("functionCall").GetProperty("args").GetProperty("command").GetString().ShouldBe("df -h /");
        parts[1].GetProperty("thoughtSignature").GetString().ShouldBe("c2lnbmF0dXJlLW9uZQ==");
        // The second of a parallel pair was never signed, and gets no placeholder.
        parts[2].TryGetProperty("thoughtSignature", out _).ShouldBeFalse();

        contents[2].GetProperty("role").GetString().ShouldBe("user");
        var results = contents[2].GetProperty("parts").EnumerateArray().ToArray();
        results[0].GetProperty("functionResponse").GetProperty("id").GetString().ShouldBe("fc_df");
        results[0].GetProperty("functionResponse").GetProperty("name").GetString().ShouldBe(AssistTools.RunCommand);
        results[0].GetProperty("functionResponse").GetProperty("response").GetProperty("output").GetString()
            .ShouldNotBeNull().ShouldContain("98%");
        results[1].GetProperty("functionResponse").GetProperty("response").GetProperty("error").GetString()
            .ShouldBe("refused");
    }

    /// <summary>
    /// A turn with no signature at all -- from a 2.5 model, or one Gemini did not
    /// write -- is a 400 on Gemini 3 unless it carries Google's placeholder.
    /// </summary>
    [Fact]
    public async Task ATurnWithNoSignatureGetsGooglesPlaceholderOnItsFirstCall()
    {
        var (_, stub) = await Stream("gemini.answer.sse", Conversation(
        [
            new AssistToolCall("fc_df", AssistTools.RunCommand, """{"command":"df -h /","why":"how full"}"""),
            new AssistToolCall("fc_du", AssistTools.RunCommand, """{"command":"du -xsh /var","why":"largest"}"""),
        ]));

        var parts = JsonDocument.Parse(stub.Body!).RootElement.GetProperty("contents")[1].GetProperty("parts")
            .EnumerateArray().ToArray();

        var sent = Convert.FromBase64String(parts[1].GetProperty("thoughtSignature").GetString()!);
        var placeholder = Convert.FromBase64String(GeminiBackend.UnsignedCall.Replace('_', '/'));
        sent.ShouldBe(placeholder);
        parts[2].TryGetProperty("thoughtSignature", out _).ShouldBeFalse();
    }

    /// <summary>
    /// Under the field that takes JSON Schema whole. The other takes an OpenAPI
    /// subset, and a connected tool's <c>additionalProperties</c> would be a 400.
    /// </summary>
    [Fact]
    public async Task ToolsAreDeclaredWithTheirJsonSchemaWhole()
    {
        var (_, stub) = await Stream("gemini.answer.sse", new AssistRequest
        {
            System = AssistPrompts.HostWithCommands,
            Messages = [new AssistMessage { Role = AssistRole.User, Text = "x" }],
            Tools =
            [
                AssistTools.Runner,
                new AssistTool("strict", "A tool with a schema Gemini's subset refuses.",
                    """{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false}"""),
            ],
        });

        var declarations = JsonDocument.Parse(stub.Body!).RootElement.GetProperty("tools")[0]
            .GetProperty("functionDeclarations").EnumerateArray().ToArray();

        declarations[0].GetProperty("name").GetString().ShouldBe(AssistTools.RunCommand);
        declarations[0].GetProperty("parametersJsonSchema").GetProperty("required")
            .EnumerateArray().Select(field => field.GetString()).ShouldBe(["command", "why"]);
        declarations[0].TryGetProperty("parameters", out _).ShouldBeFalse();
        declarations[1].GetProperty("parametersJsonSchema").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task NoToolsMeansNoToolsField()
    {
        var (_, stub) = await Stream("gemini.answer.sse", Question("x"));

        JsonDocument.Parse(stub.Body!).RootElement.TryGetProperty("tools", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ASafetyStopIsARefusalAndNotAnError()
    {
        var (events, _) = await Stream("gemini.refusal.sse", Question("x"));

        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.Refusal);
        Said(events).ShouldBe("I cannot help with that.");
    }

    [Fact]
    public async Task APromptBlockedBeforeAnyAnswerIsARefusal()
    {
        var (events, _) = await Stream("gemini.blocked.sse", Question("x"));

        events.OfType<AssistEvent.Finished>().Single().Reason.ShouldBe(AssistStop.Refusal);
        Said(events).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "API key not valid. Please pass a valid API key.", "INVALID_ARGUMENT", "API key")]
    [InlineData(HttpStatusCode.Forbidden, "Permission denied.", "PERMISSION_DENIED", "API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "Resource exhausted.", "RESOURCE_EXHAUSTED", "rate limiting")]
    [InlineData(HttpStatusCode.InternalServerError, "Internal error.", "INTERNAL", "server error")]
    [InlineData(HttpStatusCode.NotFound, "models/gemini-9 is not found.", "NOT_FOUND", "gemini-9")]
    public async Task EachFailureSaysSomethingAPersonCanActOn(HttpStatusCode status, string message, string code, string expected)
    {
        var stub = new StubEndpoint(
            $$$"""{"error":{"code":{{{(int)status}}},"message":"{{{message}}}","status":"{{{code}}}"}}""",
            status);
        using var backend = GeminiBackend.Create("not-a-real-key", "gemini-3.8-flash", "https://stub.invalid", stub.Client);

        var thrown = await Should.ThrowAsync<AssistException>(async () =>
        {
            await foreach (var _ in backend.Stream(Question("x"), TestContext.Current.CancellationToken))
            {
            }
        });

        thrown.Message.ShouldContain("Gemini");
        thrown.Message.ShouldContain(expected);
    }

    [Theory]
    [InlineData("STOP", AssistStop.EndTurn)]
    [InlineData("MAX_TOKENS", AssistStop.Length)]
    [InlineData("SAFETY", AssistStop.Refusal)]
    [InlineData("PROHIBITED_CONTENT", AssistStop.Refusal)]
    [InlineData("MALFORMED_FUNCTION_CALL", AssistStop.Other)]
    public void EveryWayOfStoppingIsTranslated(string reason, AssistStop expected) =>
        GeminiBackend.Translate(reason).ShouldBe(expected);

    private static AssistRequest Question(string text) => new()
    {
        System = AssistPrompts.Host,
        Messages = [new AssistMessage { Role = AssistRole.User, Text = text }],
    };

    /// <summary>A question, the model's calls, and their results: the second refused.</summary>
    private static AssistRequest Conversation(IReadOnlyList<AssistToolCall> calls) => new()
    {
        System = AssistPrompts.HostWithCommands,
        Tools = [AssistTools.Runner],
        Messages =
        [
            new AssistMessage { Role = AssistRole.User, Text = "what is eating the disk?" },
            new AssistMessage { Role = AssistRole.Assistant, Text = "Checking.", ToolCalls = calls },
            new AssistMessage
            {
                Role = AssistRole.User,
                ToolResults =
                [
                    new AssistToolResult(calls[0].Id, "exit status 0\n/dev/sda1 98% /"),
                    new AssistToolResult(calls[1].Id, "refused", Failed: true),
                ],
            },
        ],
    };

    private static async Task<(List<AssistEvent> Events, StubEndpoint Stub)> Stream(string fixture, AssistRequest request)
    {
        var stub = new StubEndpoint(Recorded.Text(fixture));
        using var backend = GeminiBackend.Create("not-a-real-key", "gemini-3.8-flash", "https://stub.invalid", stub.Client);

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
