using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrangeSharpTerm.Assist.Providers;

/// <summary>
/// DeepSeek, over its OpenAI-shaped chat completions endpoint.
///
/// Raw HTTP, because there is no SDK for it here and one endpoint is a smaller
/// thing to own than a dependency in the path of the user's API key. What
/// differs from Claude is all in this file: the system prompt is the first
/// message rather than a field, reasoning arrives on a delta rather than as a
/// block, tool results are messages of their own, and the stream ends with
/// <c>[DONE]</c>.
/// </summary>
public sealed class DeepSeekBackend(HttpClient http, string model, string endpoint) : IAssistBackend, IDisposable
{
    public const string DefaultEndpoint = "https://api.deepseek.com/chat/completions";

    private readonly bool _ownsClient;

    public string ProviderName => AssistProvider.DeepSeek.Name;

    public string Model => model;

    private DeepSeekBackend(HttpClient http, string model, string endpoint, bool owns)
        : this(http, model, endpoint) => _ownsClient = owns;

    public static DeepSeekBackend Create(string apiKey, string model, string? endpoint = null)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return new DeepSeekBackend(http, model, endpoint is { Length: > 0 } ? endpoint : DefaultEndpoint, owns: true);
    }

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(Body(request), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new AssistException($"{ProviderName} could not be reached. ({e.Message})", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new AssistException(Explain((int)response.StatusCode, detail));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var decoder = new DeepSeekStream();
            var ended = false;

            await foreach (var payload in Sse.Read(stream, cancellationToken))
            {
                foreach (var translated in decoder.Decode(payload))
                {
                    if (translated is AssistEvent.Finished)
                        ended = true;
                    yield return translated;
                }
                if (ended)
                    yield break;
            }

            // The connection ended without [DONE]. What arrived is still an
            // answer, so it is finished here rather than thrown away.
            foreach (var translated in decoder.Finish())
                yield return translated;
        }
    }

    /// <summary>
    /// The request body.
    ///
    /// Two details here are load-bearing and were taken from a working client
    /// rather than from the documentation, which was unreachable:
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
    internal string Body(AssistRequest request)
    {
        var messages = new JsonArray
        {
            // The system prompt is the first message here, not a field of its own.
            new JsonObject { ["role"] = "system", ["content"] = request.System },
        };

        foreach (var message in request.Messages)
        {
            // A tool result is a message of its own, one per call, and it has to
            // come before anything else the user said in the same turn.
            foreach (var result in message.ToolResults)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.CallId,
                    ["content"] = result.Output,
                });
            }

            if (message.ToolCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.Arguments,
                        },
                    });
                }
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = message.Text ?? "",
                    ["tool_calls"] = calls,
                });
                continue;
            }

            if (message.Text is { Length: > 0 } text || message.ToolResults.Count == 0)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = message.Role == AssistRole.User ? "user" : "assistant",
                    ["content"] = message.Text ?? "",
                });
            }
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
        };

        if (Thinks(model))
            body["thinking"] = new JsonObject { ["type"] = "enabled" };

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.JsonSchema),
                    },
                });
            }
            body["tools"] = tools;
        }

        return body.ToJsonString(Layout);
    }

    /// <summary>
    /// Whether this model wants the <c>thinking</c> parameter. Inferred from the
    /// name, and overridable by pointing at a different endpoint, because the
    /// inference is a guess about the future.
    /// </summary>
    internal static bool Thinks(string model) =>
        model.StartsWith("deepseek-v", StringComparison.OrdinalIgnoreCase)
        && model.Length > 10 && char.IsDigit(model[10]) && model[10] >= '4';

    private static string Explain(int status, string detail) => status switch
    {
        401 or 403 => $"{AssistProvider.DeepSeek.Name} refused the API key. Check it in Settings.",
        402 => $"{AssistProvider.DeepSeek.Name} says this account has no balance.",
        429 => $"{AssistProvider.DeepSeek.Name} is rate limiting this key. Try again shortly.",
        >= 500 => $"{AssistProvider.DeepSeek.Name} had a server error ({status}).",
        _ => $"{AssistProvider.DeepSeek.Name} rejected the request ({status}). {Trim(detail)}",
    };

    /// <summary>A provider's error body can be a page. The first line of it is the part worth showing.</summary>
    private static string Trim(string detail) =>
        detail.Length <= 300 ? detail : detail[..300] + "…";

    public void Dispose()
    {
        if (_ownsClient)
            http.Dispose();
    }

    private static readonly JsonSerializerOptions Layout = new() { WriteIndented = false };
}
