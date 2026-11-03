using System.Net;
using System.Text;
using System.Text.Json;
using StrangeSharpTerm.Assist.Providers;

namespace StrangeSharpTerm.Assist.Tests;

public class AltheiaBackendTests
{
    private const string Fast = "qwen3.8-27b-fast";

    [Fact]
    public async Task AnAnswerComesBackUnderTheModelThatWasAskedFor()
    {
        var stub = new StubEndpoint(Recorded.Text("altheia.answer.sse"));
        var backend = Backend(stub);

        var events = await Read(backend);

        string.Concat(events.OfType<AssistEvent.Say>().Select(say => say.Text))
            .ShouldBe("The journal is 37G of a 49G filesystem.");
        events.OfType<AssistEvent.Spent>().ShouldHaveSingleItem().Usage.Output.ShouldBe(14);
        // The stream calls it qwen3.8-27b-fp8, the same model's internal name.
        // The header goes on naming the one that was chosen.
        backend.Model.ShouldBe(Fast);
    }

    /// <summary>Its quick start: always cap an answer, and leave temperature to the server.</summary>
    [Fact]
    public async Task TheBodyCapsTheAnswerAndLeavesTemperatureAlone()
    {
        var stub = new StubEndpoint(Recorded.Text("altheia.answer.sse"));

        await Read(Backend(stub));

        var body = JsonDocument.Parse(stub.Body!).RootElement;
        body.GetProperty("model").GetString().ShouldBe(Fast);
        body.GetProperty("max_tokens").GetInt32().ShouldBe(AltheiaBackend.MaxAnswerTokens);
        body.TryGetProperty("temperature", out _).ShouldBeFalse();
        // DeepSeek's, and nothing to do with this server.
        body.TryGetProperty("thinking", out _).ShouldBeFalse();
    }

    [Fact]
    public void WithNoOverrideItTalksToTheCompanyServer() =>
        AltheiaBackend.DefaultEndpoint.ShouldBe("https://altheia/v1/chat/completions");

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_api_key"}}""", "API key")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"model_not_allowed"}}""", "qwen3.8-27b-fast")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"context_length_exceeded"}}""", "Start a new one")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":"quota_exceeded"}}""", "daily")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":"concurrency_limit_exceeded"}}""", "another app")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "server error")]
    public async Task EachDocumentedFailureSaysWhatToDo(HttpStatusCode status, string body, string expected)
    {
        var thrown = await Should.ThrowAsync<AssistException>(() => Read(Backend(new StubEndpoint(body, status))));

        thrown.Message.ShouldContain(expected);
    }

    [Fact]
    public async Task NotBeingReachableNamesTheTwoThingsThatCauseIt()
    {
        using var unreachable = new HttpClient(new Refused());
        var backend = new AltheiaBackend(unreachable, Fast, "https://stub.invalid/", new SemaphoreSlim(1));

        var thrown = await Should.ThrowAsync<AssistException>(() => Read(backend));

        thrown.Message.ShouldContain("VPN");
        thrown.Message.ShouldContain("certificate");
    }

    /// <summary>
    /// A key may have two requests in flight and a fan-out runs three hosts at
    /// once, so the third has to wait rather than come back as a 429.
    /// </summary>
    [Fact]
    public async Task AThirdRequestWaitsForASlotInsteadOfBeingSent()
    {
        using var gate = new SemaphoreSlim(AltheiaBackend.MaxInFlight);
        using var held = new Held();
        using var client = new HttpClient(held);
        var backend = new AltheiaBackend(client, Fast, "https://stub.invalid/", gate);

        var reads = Enumerable.Range(0, 3).Select(_ => Task.Run(() => Read(backend))).ToArray();

        await held.Arrived(2);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        held.Sent.ShouldBe(2);

        held.Open();
        await Task.WhenAll(reads);

        held.Sent.ShouldBe(3);
        gate.CurrentCount.ShouldBe(AltheiaBackend.MaxInFlight);
    }

    [Fact]
    public async Task AFailedRequestGivesItsSlotBack()
    {
        using var gate = new SemaphoreSlim(1);
        var stub = new StubEndpoint("{}", HttpStatusCode.InternalServerError);
        var backend = new AltheiaBackend(stub.Client(), Fast, "https://stub.invalid/", gate);

        await Should.ThrowAsync<AssistException>(() => Read(backend));

        gate.CurrentCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReadingOnlyPartOfAnAnswerGivesItsSlotBack()
    {
        using var gate = new SemaphoreSlim(1);
        var stub = new StubEndpoint(Recorded.Text("altheia.answer.sse"));
        var backend = new AltheiaBackend(stub.Client(), Fast, "https://stub.invalid/", gate);

        await foreach (var _ in backend.Stream(Question(), TestContext.Current.CancellationToken))
            break;

        gate.CurrentCount.ShouldBe(1);
    }

    [Fact]
    public async Task StoppingWhileQueuedLeavesTheOthersSlotsAlone()
    {
        using var gate = new SemaphoreSlim(0);
        var stub = new StubEndpoint(Recorded.Text("altheia.answer.sse"));
        var backend = new AltheiaBackend(stub.Client(), Fast, "https://stub.invalid/", gate);
        using var stop = new CancellationTokenSource();

        var queued = ReadUntil(backend, stop.Token);
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => queued);
        stub.Body.ShouldBeNull();
        gate.CurrentCount.ShouldBe(0);
    }

    private static AltheiaBackend Backend(StubEndpoint stub) =>
        new(stub.Client(), Fast, "https://stub.invalid/v1/chat/completions", new SemaphoreSlim(AltheiaBackend.MaxInFlight));

    private static AssistRequest Question() => new()
    {
        System = AssistPrompts.Host,
        Messages = [new AssistMessage { Role = AssistRole.User, Text = "is the disk full?" }],
    };

    private static Task<List<AssistEvent>> Read(AltheiaBackend backend) =>
        ReadUntil(backend, TestContext.Current.CancellationToken);

    private static async Task<List<AssistEvent>> ReadUntil(AltheiaBackend backend, CancellationToken cancellationToken)
    {
        var events = new List<AssistEvent>();
        await foreach (var streamed in backend.Stream(Question(), cancellationToken))
            events.Add(streamed);
        return events;
    }

    private sealed class Refused : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The SSL connection could not be established");
    }

    /// <summary>A server that holds every request open until it is told to answer.</summary>
    private sealed class Held : HttpMessageHandler
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _arrivals = new(0);
        private int _sent;

        internal int Sent => Volatile.Read(ref _sent);

        internal async Task Arrived(int count)
        {
            for (var i = 0; i < count; i++)
                await _arrivals.WaitAsync(TimeSpan.FromSeconds(10));
        }

        internal void Open() => _open.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            _arrivals.Release();
            await _open.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Recorded.Text("altheia.answer.sse"), Encoding.UTF8, "text/event-stream"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _arrivals.Dispose();
            base.Dispose(disposing);
        }
    }
}
