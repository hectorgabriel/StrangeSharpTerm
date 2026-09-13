using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace StrangeSharpTerm.Assist.Providers;

/// <summary>
/// Claude, through the official SDK.
///
/// The Swift app spoke to both providers over raw HTTP, for the good reason that
/// Swift has no Anthropic SDK. C# does, and the parts of the request this app
/// depends on are the parts that have moved between model generations: adaptive
/// thinking replacing a token budget, a refusal arriving as a stop reason rather
/// than an error, and a tool call streamed as partial JSON. Following that is
/// what an SDK is for. See docs/adr/0006.
///
/// What does not change is the seam. Everything above <see cref="IAssistBackend"/>
/// is the same for both providers, and this file is one of the two places in the
/// app that knows a provider's name.
/// </summary>
public sealed class ClaudeBackend(AnthropicClient client, string model) : IAssistBackend
{
    /// <summary>
    /// Room for the answer. Generous because a truncated answer costs a whole
    /// exchange, and because this streams, so a large ceiling is not a timeout
    /// waiting to happen.
    /// </summary>
    private const int MaxTokens = 16000;

    public string ProviderName => AssistProvider.Claude.Name;

    public string Model => model;

    /// <param name="endpoint">
    /// A different base address, which is how the wire format is checked against
    /// a stub without an account. Null is Anthropic's own.
    /// </param>
    public static ClaudeBackend Create(string apiKey, string model, string? endpoint = null)
    {
        // BaseUrl is init-only and not nullable, so an absent override means
        // not naming it at all rather than naming it null.
        var client = endpoint is { Length: > 0 } elsewhere
            ? new AnthropicClient { ApiKey = apiKey, BaseUrl = elsewhere }
            : new AnthropicClient { ApiKey = apiKey };
        return new ClaudeBackend(client, model);
    }

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            System = request.System,
            Messages = [.. request.Messages.Select(Translate)],
            // Summarised rather than omitted: a pane that is reasoning should
            // show that it is rather than looking frozen, and the default on
            // these models returns the blocks empty.
            Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },
            Tools = [.. request.Tools.Select(Translate)],
        };

        // The call itself is where a bad key, an unreachable host or a rate
        // limit arrives, and all three have to reach the pane as a sentence.
        IAsyncEnumerator<RawMessageStreamEvent> events;
        try
        {
            events = client.Messages.CreateStreaming(parameters, cancellationToken: cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new AssistException(Explain(e), e);
        }

        // A tool call arrives as partial JSON across several deltas, so it is
        // assembled here and emitted whole: half an argument is no use to the
        // gate, which has to judge the command before anything runs.
        var building = new Dictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        var stop = AssistStop.EndTurn;

        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await events.MoveNextAsync();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new AssistException(Explain(e), e);
                }
                if (!more)
                    break;

                foreach (var translated in Interpret(events.Current, building, ref stop))
                    yield return translated;
            }
        }
        finally
        {
            await events.DisposeAsync();
        }

        yield return new AssistEvent.Finished(stop);
    }

    private static IEnumerable<AssistEvent> Interpret(
        RawMessageStreamEvent raw,
        Dictionary<int, (string Id, string Name, StringBuilder Arguments)> building,
        ref AssistStop stop)
    {
        if (raw.TryPickContentBlockStart(out var start))
        {
            if (start.ContentBlock.TryPickToolUse(out var tool))
                building[(int)start.Index] = (tool.ID, tool.Name, new StringBuilder());
            return [];
        }

        if (raw.TryPickContentBlockDelta(out var delta))
        {
            if (delta.Delta.TryPickText(out var text))
                return [new AssistEvent.Say(text.Text)];
            if (delta.Delta.TryPickThinking(out var thinking))
                return [new AssistEvent.Reasoning(thinking.Thinking)];
            if (delta.Delta.TryPickInputJson(out var json)
                && building.TryGetValue((int)delta.Index, out var partial))
            {
                partial.Arguments.Append(json.PartialJson);
            }
            return [];
        }

        if (raw.TryPickContentBlockStop(out var finished)
            && building.Remove((int)finished.Index, out var complete))
        {
            // An empty argument list streams as no deltas at all, and "{}" is
            // what the schema means by that.
            var arguments = complete.Arguments.Length == 0 ? "{}" : complete.Arguments.ToString();
            return [new AssistEvent.Call(new AssistToolCall(complete.Id, complete.Name, arguments))];
        }

        if (raw.TryPickDelta(out var message) && message.Delta.StopReason is { } reason)
            stop = Translate(reason);

        return [];
    }

    /// <summary>
    /// Why the model stopped, in the terms the loop above understands.
    ///
    /// A refusal is a stop reason rather than an error: the provider declined
    /// server-side, the request was fine, and the pane has something to say.
    /// </summary>
    private static AssistStop Translate(StopReason reason) => reason.ToString() switch
    {
        "ToolUse" or "tool_use" => AssistStop.ToolUse,
        "MaxTokens" or "max_tokens" => AssistStop.Length,
        "Refusal" or "refusal" => AssistStop.Refusal,
        "EndTurn" or "end_turn" or "StopSequence" or "stop_sequence" => AssistStop.EndTurn,
        _ => AssistStop.Other,
    };

    private static MessageParam Translate(AssistMessage message)
    {
        if (message.Role == AssistRole.User)
        {
            // Results go back as content blocks on a user message, which is the
            // shape this API wants and not the shape DeepSeek wants. That
            // difference is the whole reason for the seam.
            if (message.ToolResults.Count > 0)
            {
                List<ContentBlockParam> blocks =
                [
                    .. message.ToolResults.Select(result => (ContentBlockParam)new ToolResultBlockParam
                    {
                        ToolUseID = result.CallId,
                        Content = result.Output,
                        IsError = result.Failed,
                    }),
                ];
                if (message.Text is { Length: > 0 } trailing)
                    blocks.Add(new TextBlockParam { Text = trailing });
                return new MessageParam { Role = Role.User, Content = blocks };
            }

            return new MessageParam { Role = Role.User, Content = message.Text ?? "" };
        }

        List<ContentBlockParam> content = [];
        if (message.Text is { Length: > 0 } said)
            content.Add(new TextBlockParam { Text = said });
        foreach (var call in message.ToolCalls)
        {
            content.Add(new ToolUseBlockParam
            {
                ID = call.Id,
                Name = call.Name,
                Input = Fields(call.Arguments),
            });
        }
        return new MessageParam { Role = Role.Assistant, Content = content };
    }

    private static ToolUnion Translate(AssistTool tool)
    {
        using var schema = JsonDocument.Parse(tool.JsonSchema);
        var root = schema.RootElement;
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new InputSchema
            {
                Properties = root.TryGetProperty("properties", out var properties)
                    ? properties.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone())
                    : [],
                Required = root.TryGetProperty("required", out var required)
                    ? [.. required.EnumerateArray().Select(field => field.GetString() ?? "")]
                    : [],
            },
        };
    }

    /// <summary>
    /// A tool call's arguments as this API wants them back: the object's fields,
    /// not the object. They are echoed unparsed apart from this, because what was
    /// asked for is what has to go back.
    /// </summary>
    private static Dictionary<string, JsonElement> Fields(string json)
    {
        using var document = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone())
            : [];
    }

    /// <summary>
    /// A provider failure as a person can act on it.
    ///
    /// The distinction worth keeping is between "your key is wrong" and "the
    /// network is down", because only one of them is fixed in Settings.
    /// </summary>
    private static string Explain(Exception e) => e switch
    {
        HttpRequestException => $"{AssistProvider.Claude.Name} could not be reached. ({e.Message})",
        TaskCanceledException or TimeoutException => $"{AssistProvider.Claude.Name} did not answer in time.",
        _ when e.Message.Contains("401", StringComparison.Ordinal)
            || e.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) =>
            $"{AssistProvider.Claude.Name} refused the API key. Check it in Settings.",
        _ when e.Message.Contains("429", StringComparison.Ordinal) =>
            $"{AssistProvider.Claude.Name} is rate limiting this key. Try again shortly.",
        _ => $"{AssistProvider.Claude.Name} could not answer. ({e.Message})",
    };
}
