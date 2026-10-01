using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;

namespace StrangeSharpTerm.Assist.Providers;

/// <summary>
/// Gemini, through Google's own SDK, for the reason Claude goes through
/// Anthropic's: see docs/adr/0006.
///
/// What differs from the other two is all in this file. The system prompt is a
/// field of the config; the assistant is called <c>model</c>; a tool result
/// names the function it answers, not just the call; reasoning is a part marked
/// <c>thought</c>; and a tool call carries a thought signature that has to go
/// back exactly as it came, or Gemini 3 answers the next request with a 400.
/// </summary>
public sealed class GeminiBackend(Client client, string model) : IAssistBackend, IDisposable
{
    /// <summary>
    /// What a call that never had a signature is sent with instead. Google's own
    /// escape hatch, for a call the API did not produce -- one replayed from a
    /// Gemini 2.5 conversation, say. Without it that turn is a 400; with it the
    /// model answers, a little less well.
    /// </summary>
    internal const string UnsignedCall = "skip_thought_signature_validator";

    public string ProviderName => AssistProvider.Gemini.Name;

    public string Model => model;

    /// <param name="endpoint">
    /// A different base address, which is how the wire format is checked against
    /// a stub without an account. Null is Google's own.
    /// </param>
    /// <param name="http">Where requests go. Tests hand in a stub; the app leaves it to the SDK.</param>
    public static GeminiBackend Create(string apiKey, string model, string? endpoint = null, Func<HttpClient>? http = null)
    {
        // Both flags said out loud: left unset, the SDK reads them from the
        // environment, and a variable meant for some other tool would send this
        // key and this terminal's output to Vertex instead.
        var client = new Client(
            enterprise: false,
            vertexAI: false,
            apiKey: apiKey,
            httpOptions: endpoint is { Length: > 0 } elsewhere ? new HttpOptions { BaseUrl = elsewhere.TrimEnd('/') } : null,
            clientOptions: http is null ? null : new ClientOptions { HttpClientFactory = http });
        return new GeminiBackend(client, model);
    }

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var config = new GenerateContentConfig
        {
            SystemInstruction = new Content { Parts = [new Part { Text = request.System }] },
            // Summaries rather than nothing, so a pane that is reasoning shows it.
            // The level is left to the model: which levels a model accepts
            // differs across the 3.x line, and a wrong one is a 400.
            ThinkingConfig = new ThinkingConfig { IncludeThoughts = true },
            Tools = request.Tools.Count == 0
                ? null
                : [new Tool { FunctionDeclarations = [.. request.Tools.Select(Translate)] }],
        };

        // The call itself is where a bad key, an unreachable host or a rate
        // limit arrives, and all three have to reach the pane as a sentence.
        IAsyncEnumerator<GenerateContentResponse> chunks;
        try
        {
            chunks = client.Models.GenerateContentStreamAsync(model, Translate(request.Messages), config, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new AssistException(Explain(e), e);
        }

        var stop = AssistStop.EndTurn;
        var called = false;

        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await chunks.MoveNextAsync();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new AssistException(Explain(e), e);
                }
                if (!more)
                    break;

                var chunk = chunks.Current;

                // A prompt blocked before any answer comes back as feedback with
                // no candidates at all. The request was fine; Google declined it.
                if (chunk.PromptFeedback?.BlockReason is not null)
                    stop = AssistStop.Refusal;

                if (chunk.Candidates is not [var candidate, ..])
                    continue;

                foreach (var part in candidate.Content?.Parts ?? [])
                {
                    // A call arrives whole in one part, signature and all, so
                    // there is nothing to assemble here as there is for the others.
                    if (part.FunctionCall is { } call)
                    {
                        called = true;
                        yield return new AssistEvent.Call(new AssistToolCall(
                            call.Id is { Length: > 0 } id ? id : $"call_{Guid.NewGuid():N}",
                            call.Name ?? "",
                            call.Args is null ? "{}" : JsonSerializer.Serialize(call.Args),
                            part.ThoughtSignature is { Length: > 0 } signature ? Convert.ToBase64String(signature) : null));
                    }
                    else if (part.Text is { Length: > 0 } text)
                    {
                        yield return part.Thought == true ? new AssistEvent.Reasoning(text) : new AssistEvent.Say(text);
                    }
                }

                if (candidate.FinishReason is { } finish)
                    stop = Translate(finish.Value);
            }
        }
        finally
        {
            await chunks.DisposeAsync();
        }

        // Gemini finishes a turn that ends in calls with a plain STOP. The loop
        // above runs the calls on ToolUse, so that is what it is told.
        yield return new AssistEvent.Finished(called && stop == AssistStop.EndTurn ? AssistStop.ToolUse : stop);
    }

    /// <summary>
    /// The transcript as Gemini wants it.
    ///
    /// A function response has to name the function it answers, and the
    /// transcript keeps only the call's id beside a result, so the names are
    /// read off the calls as the history is walked.
    /// </summary>
    internal static List<Content> Translate(IReadOnlyList<AssistMessage> messages)
    {
        var names = new Dictionary<string, string>();
        var contents = new List<Content>();

        foreach (var message in messages)
        {
            var parts = new List<Part>();

            if (message.Role == AssistRole.User)
            {
                // Results first, then anything the user said in the same turn.
                foreach (var result in message.ToolResults)
                {
                    parts.Add(new Part
                    {
                        FunctionResponse = new FunctionResponse
                        {
                            Id = result.CallId,
                            Name = names.GetValueOrDefault(result.CallId, ""),
                            Response = new Dictionary<string, object>
                            {
                                [result.Failed ? "error" : "output"] = result.Output,
                            },
                        },
                    });
                }
                if (message.Text is { Length: > 0 } said)
                    parts.Add(new Part { Text = said });
            }
            else
            {
                if (message.Text is { Length: > 0 } said)
                    parts.Add(new Part { Text = said });

                // Of parallel calls only the first is signed, and that is the one
                // Gemini checks. A turn with none signed gets the placeholder on
                // its first call, which is where the check looks.
                var signed = message.ToolCalls.Any(call => call.Signature is not null);
                for (var i = 0; i < message.ToolCalls.Count; i++)
                {
                    var call = message.ToolCalls[i];
                    names[call.Id] = call.Name;
                    var signature = call.Signature ?? (!signed && i == 0 ? UnsignedCall : null);
                    parts.Add(new Part
                    {
                        FunctionCall = new FunctionCall { Id = call.Id, Name = call.Name, Args = Fields(call.Arguments) },
                        ThoughtSignature = signature is null ? null : Bytes(signature),
                    });
                }
            }

            // An empty turn is a 400 here, and says nothing anyway.
            if (parts.Count > 0)
                contents.Add(new Content { Role = message.Role == AssistRole.User ? "user" : "model", Parts = parts });
        }

        return contents;
    }

    /// <summary>
    /// The schema as it was given, under the field that takes JSON Schema whole.
    /// The other one takes an OpenAPI subset and refuses keywords a connected
    /// tool's schema is free to use, such as <c>additionalProperties</c>.
    /// </summary>
    private static FunctionDeclaration Translate(AssistTool tool)
    {
        using var schema = JsonDocument.Parse(tool.JsonSchema);
        return new FunctionDeclaration
        {
            Name = tool.Name,
            Description = tool.Description,
            ParametersJsonSchema = schema.RootElement.Clone(),
        };
    }

    /// <summary>
    /// Why it stopped. The several ways Google has of declining are all a
    /// refusal: the request was fine and the provider said no, which the pane
    /// says differently from an error.
    /// </summary>
    internal static AssistStop Translate(string? reason) => reason switch
    {
        "STOP" => AssistStop.EndTurn,
        "MAX_TOKENS" => AssistStop.Length,
        "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" => AssistStop.Refusal,
        _ => AssistStop.Other,
    };

    /// <summary>A call's arguments as the SDK wants them back: the object's fields, not the object.</summary>
    private static Dictionary<string, object> Fields(string json)
    {
        using var document = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value.Clone())
            : [];
    }

    /// <summary>
    /// A signature back to the bytes it came as. The placeholder is written in
    /// the URL-safe alphabet, which the server reads as the same bytes as the
    /// standard one the SDK writes them out in.
    /// </summary>
    private static byte[] Bytes(string signature) =>
        Convert.FromBase64String(signature.Replace('-', '+').Replace('_', '/'));

    /// <summary>
    /// A provider failure as a person can act on it. A bad key is a 400 here,
    /// not a 401, so the message is what tells it apart from a bad request.
    /// </summary>
    private static string Explain(Exception e) => e switch
    {
        ApiException { StatusCode: 401 or 403 } => $"{AssistProvider.Gemini.Name} refused the API key. Check it in Settings.",
        ApiException { StatusCode: 400 } when e.Message.Contains("API key", StringComparison.OrdinalIgnoreCase) =>
            $"{AssistProvider.Gemini.Name} refused the API key. Check it in Settings.",
        ApiException { StatusCode: 429 } => $"{AssistProvider.Gemini.Name} is rate limiting this key. Try again shortly.",
        ApiException { StatusCode: >= 500 } api => $"{AssistProvider.Gemini.Name} had a server error ({api.StatusCode}).",
        ApiException api => $"{AssistProvider.Gemini.Name} rejected the request ({api.StatusCode}). {Trim(api.Message)}",
        HttpRequestException => $"{AssistProvider.Gemini.Name} could not be reached. ({e.Message})",
        TaskCanceledException or TimeoutException => $"{AssistProvider.Gemini.Name} did not answer in time.",
        _ => $"{AssistProvider.Gemini.Name} could not answer. ({e.Message})",
    };

    private static string Trim(string detail) =>
        detail.Length <= 300 ? detail : detail[..300] + "…";

    public void Dispose() => client.Dispose();
}
