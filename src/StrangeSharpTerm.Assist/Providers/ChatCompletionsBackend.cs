using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrangeSharpTerm.Assist.Providers;

/// <summary>
/// A provider reached over an OpenAI-shaped chat completions endpoint.
///
/// Raw HTTP, because there is no SDK for it here and one endpoint is a smaller
/// thing to own than a dependency in the path of the user's API key. What
/// differs from Claude is all in this file: the system prompt is the first
/// message rather than a field, reasoning arrives on a delta rather than as a
/// block, tool results are messages of their own, and the stream ends with
/// <c>[DONE]</c>.
///
/// What differs between the servers that speak it is what a subclass says: what
/// else the body carries, and what each failure means there.
/// </summary>
public abstract class ChatCompletionsBackend(HttpClient http, string model, string endpoint) : IAssistBackend, IDisposable
{
    private bool _ownsClient;

    public abstract string ProviderName { get; }

    public string Model => model;

    /// <summary>A client carrying this key, for a backend that is to dispose of it.</summary>
    protected static HttpClient ClientFor(string apiKey)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    /// <summary>Makes the client this backend's to dispose of, which one handed in by a test is not.</summary>
    protected void TakeOwnershipOfClient() => _ownsClient = true;

    public virtual async IAsyncEnumerable<AssistEvent> Stream(
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
            throw new AssistException(Unreachable(e), e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new AssistException(Explain((int)response.StatusCode, detail));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var decoder = new DeepSeekStream(ProviderName);
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

    /// <summary>The request body, as every server of this shape reads it.</summary>
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
            // Without this a stream reports no usage at all. With it, a last
            // chunk with no choices carries the request's counts, the cache's
            // share included -- this API caches a repeated prefix on its own.
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };

        AddTo(body);

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

    /// <summary>What this server wants in the body beyond what they all share.</summary>
    protected virtual void AddTo(JsonObject body)
    {
    }

    /// <summary>What a person is told when the request never got an answer.</summary>
    protected virtual string Unreachable(Exception e) => $"{ProviderName} could not be reached. ({e.Message})";

    /// <summary>What a refused request means here, in a sentence a person can act on.</summary>
    protected abstract string Explain(int status, string detail);

    /// <summary>A provider's error body can be a page. The first line of it is the part worth showing.</summary>
    protected static string Trim(string detail) =>
        detail.Length <= 300 ? detail : detail[..300] + "…";

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && _ownsClient)
            http.Dispose();
    }

    private static readonly JsonSerializerOptions Layout = new() { WriteIndented = false };
}
