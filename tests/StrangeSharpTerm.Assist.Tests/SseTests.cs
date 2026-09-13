using System.Text;
using StrangeSharpTerm.Assist.Providers;

namespace StrangeSharpTerm.Assist.Tests;

public class SseTests
{
    [Fact]
    public async Task EachDataLineIsOnePayload()
    {
        var payloads = await Read("data: one\n\ndata: two\n\ndata: [DONE]\n\n");

        payloads.ShouldBe(["one", "two", "[DONE]"]);
    }

    [Fact]
    public async Task ACommentIsSkipped()
    {
        var payloads = await Read(": keep-alive\n\ndata: one\n\n");

        payloads.ShouldBe(["one"]);
    }

    [Fact]
    public async Task APayloadSplitAcrossLinesIsRejoined()
    {
        var payloads = await Read("data: {\ndata: }\n\n");

        payloads.ShouldBe(["{\n}"]);
    }

    [Fact]
    public async Task AStreamThatEndedWithoutItsBlankLineStillDelivered()
    {
        var payloads = await Read("data: one");

        payloads.ShouldBe(["one"]);
    }

    [Fact]
    public async Task TheSpaceAfterTheColonIsOptional()
    {
        var payloads = await Read("data:one\n\n");

        payloads.ShouldBe(["one"]);
    }

    private static async Task<List<string>> Read(string stream)
    {
        var payloads = new List<string>();
        await foreach (var payload in Sse.Read(new MemoryStream(Encoding.UTF8.GetBytes(stream))))
            payloads.Add(payload);
        return payloads;
    }
}
