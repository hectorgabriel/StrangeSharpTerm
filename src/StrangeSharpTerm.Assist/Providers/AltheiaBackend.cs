using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace StrangeSharpTerm.Assist.Providers;

/// <summary>
/// AltheIA, the model Grupo Reyes runs on its own servers, over its chat
/// completions endpoint.
///
/// Three things follow from its quick start. <c>max_tokens</c> is always sent,
/// as it asks. <c>temperature</c> is never sent, because zero makes this model
/// repeat itself without end and the server's own default is the recommended
/// one. And a key may have only two requests in flight, which a fan-out over
/// three hosts would exceed.
///
/// The certificate is the company's own, and it is verified like any other: the
/// machine has to trust it, and this client does not get to decide otherwise.
/// </summary>
public sealed class AltheiaBackend(HttpClient http, string model, string endpoint, SemaphoreSlim? gate = null)
    : ChatCompletionsBackend(http, model, endpoint)
{
    public const string DefaultEndpoint = "https://altheia/v1/chat/completions";

    /// <summary>
    /// A ceiling on one answer, not a target. At the 40 to 50 tokens a second
    /// this shared model gives a request, it is about three minutes of output.
    /// </summary>
    internal const int MaxAnswerTokens = 8192;

    /// <summary>Requests a key may have in flight, by AltheIA's default.</summary>
    internal const int MaxInFlight = 2;

    /// <summary>
    /// Shared by every backend in the process, because the limit belongs to the
    /// key rather than to a pane: two conversations and a fan-out all count.
    /// </summary>
    private static readonly SemaphoreSlim Shared = new(MaxInFlight);

    private readonly SemaphoreSlim _gate = gate ?? Shared;

    public override string ProviderName => AssistProvider.Altheia.Name;

    public static AltheiaBackend Create(string apiKey, string model, string? endpoint = null)
    {
        var backend = new AltheiaBackend(
            ClientFor(apiKey), model, endpoint is { Length: > 0 } ? endpoint : DefaultEndpoint);
        backend.TakeOwnershipOfClient();
        return backend;
    }

    /// <summary>
    /// The request, once there is room for it. A third one waits its turn rather
    /// than coming back as a 429 that fails its host for no reason of its own.
    /// </summary>
    public override async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await foreach (var streamed in base.Stream(request, cancellationToken))
                yield return streamed;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void AddTo(JsonObject body) => body["max_tokens"] = MaxAnswerTokens;

    /// <summary>
    /// Said here because the name resolves only inside the company, and the
    /// certificate is its own: those are the two reasons this fails, and neither
    /// is obvious from a socket error.
    /// </summary>
    protected override string Unreachable(Exception e) =>
        $"{ProviderName} could not be reached. It is only on the company network or VPN, and this machine has to trust its certificate. ({e.Message})";

    /// <summary>
    /// By the codes the quick start documents, read from the body because the
    /// status alone runs several different problems together: a 429 is either
    /// "wait a moment" or "come back tomorrow".
    /// </summary>
    protected override string Explain(int status, string detail) => status switch
    {
        400 when detail.Contains("context_length_exceeded", StringComparison.Ordinal) =>
            $"This conversation no longer fits in {ProviderName}'s context window. Start a new one.",
        403 when detail.Contains("model_not_allowed", StringComparison.Ordinal) =>
            $"{ProviderName} does not offer {Model} to this key. Check the model's name in Settings.",
        401 or 403 => $"{ProviderName} refused the API key. Check it in Settings.",
        429 when detail.Contains("quota_exceeded", StringComparison.Ordinal) =>
            $"This key has used up its daily {ProviderName} quota.",
        429 when detail.Contains("concurrency_limit_exceeded", StringComparison.Ordinal) =>
            $"{ProviderName} is already answering as many requests as this key allows, perhaps for another app using it. Try again shortly.",
        429 => $"{ProviderName} is rate limiting this key. Try again shortly.",
        >= 500 => $"{ProviderName} had a server error ({status}).",
        _ => $"{ProviderName} rejected the request ({status}). {Trim(detail)}",
    };
}
