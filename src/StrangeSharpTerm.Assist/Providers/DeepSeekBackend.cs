using System.Text.Json.Nodes;

namespace StrangeSharpTerm.Assist.Providers;

/// <summary>
/// DeepSeek, over its chat completions endpoint.
///
/// Two details are load-bearing and were taken from a working client rather than
/// from the documentation, which was unreachable:
///
/// V4 models <em>require</em> a <c>thinking</c> parameter and earlier ones
/// reject it, so it is sent only for <c>deepseek-v*</c> names. That is an
/// inference, and the day a name arrives that breaks the pattern it is
/// wrong; the endpoint override is how that day is survived.
///
/// <c>max_tokens</c> is omitted. The documented ceiling has moved with every
/// model generation and a value above it is a 400, while sending none lets
/// the server apply its own.
/// </summary>
public sealed class DeepSeekBackend(HttpClient http, string model, string endpoint)
    : ChatCompletionsBackend(http, model, endpoint)
{
    public const string DefaultEndpoint = "https://api.deepseek.com/chat/completions";

    public override string ProviderName => AssistProvider.DeepSeek.Name;

    public static DeepSeekBackend Create(string apiKey, string model, string? endpoint = null)
    {
        var backend = new DeepSeekBackend(
            ClientFor(apiKey), model, endpoint is { Length: > 0 } ? endpoint : DefaultEndpoint);
        backend.TakeOwnershipOfClient();
        return backend;
    }

    protected override void AddTo(JsonObject body)
    {
        if (Thinks(Model))
            body["thinking"] = new JsonObject { ["type"] = "enabled" };
    }

    /// <summary>
    /// Whether this model wants the <c>thinking</c> parameter. Inferred from the
    /// name, and overridable by pointing at a different endpoint, because the
    /// inference is a guess about the future.
    /// </summary>
    internal static bool Thinks(string model) =>
        model.StartsWith("deepseek-v", StringComparison.OrdinalIgnoreCase)
        && model.Length > 10 && char.IsDigit(model[10]) && model[10] >= '4';

    protected override string Explain(int status, string detail) => status switch
    {
        401 or 403 => $"{ProviderName} refused the API key. Check it in Settings.",
        402 => $"{ProviderName} says this account has no balance.",
        429 => $"{ProviderName} is rate limiting this key. Try again shortly.",
        >= 500 => $"{ProviderName} had a server error ({status}).",
        _ => $"{ProviderName} rejected the request ({status}). {Trim(detail)}",
    };
}
