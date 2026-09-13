using System.Text;
using System.Text.Json;

namespace StrangeSharpTerm.Assist.Providers;

/// <summary>
/// Turns a chat-completions stream into <see cref="AssistEvent"/>s.
///
/// Separate from <see cref="DeepSeekBackend"/> so that a recorded stream can be
/// decoded without a socket. That is how the wire format is checked at all: the
/// documentation for this API was unreachable when the Swift app was written and
/// two of the details below came from a working client rather than from it.
/// </summary>
internal sealed class DeepSeekStream
{
    /// <summary>
    /// Tool calls assemble across deltas, keyed by the index the wire gives
    /// them. A call streams its id and name once and its arguments a few
    /// characters at a time.
    /// </summary>
    private readonly Dictionary<int, Call> _calls = [];

    private AssistStop _stop = AssistStop.EndTurn;

    private sealed record Call(string Id, string Name)
    {
        internal StringBuilder Arguments { get; } = new();

        /// <summary>A name can arrive in pieces too, so it is rebuilt rather than fixed at the start.</summary>
        internal StringBuilder FullName { get; } = new(Name);
    }

    /// <summary>
    /// Decodes one <c>data:</c> payload. Returns what it produced, which is
    /// usually nothing: most chunks are a few characters of an answer.
    /// </summary>
    internal IEnumerable<AssistEvent> Decode(string payload)
    {
        if (payload == Sse.Done)
        {
            // The stream is over. Any tool call still being assembled is
            // complete -- this API sends no per-block stop -- so it is emitted
            // here, before the finish.
            foreach (var call in Flush())
                yield return call;
            yield return new AssistEvent.Finished(_stop);
            yield break;
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(payload).RootElement.Clone();
        }
        catch (JsonException)
        {
            // A chunk that is not JSON is not worth ending a conversation over.
            yield break;
        }

        // An error can arrive mid-stream with a 200 already sent, which is the
        // only way this API reports a model that refused or a quota that ran out.
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;
            throw new AssistException(
                $"{AssistProvider.DeepSeek.Name} could not answer. ({message ?? "no reason given"})");
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            yield break;

        var choice = choices[0];

        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            _stop = Translate(finish.GetString());

        if (!choice.TryGetProperty("delta", out var delta))
            yield break;

        // Reasoning arrives on a field of its own rather than as a block, which
        // is the shape difference that matters between the two providers.
        if (delta.TryGetProperty("reasoning_content", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String
            && reasoning.GetString() is { Length: > 0 } thought)
        {
            yield return new AssistEvent.Reasoning(thought);
        }

        if (delta.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String
            && content.GetString() is { Length: > 0 } said)
        {
            yield return new AssistEvent.Say(said);
        }

        if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
                Accumulate(call);
        }
    }

    /// <summary>
    /// Whatever is left when the stream ends without a <c>[DONE]</c>.
    ///
    /// A connection that drops mid-answer still delivered what it delivered, and
    /// a half-built tool call is dropped rather than run: an argument list that
    /// was never finished is not a command anyone asked for.
    /// </summary>
    internal IEnumerable<AssistEvent> Finish()
    {
        foreach (var call in Flush())
            yield return call;
        yield return new AssistEvent.Finished(_stop);
    }

    private void Accumulate(JsonElement call)
    {
        var index = call.TryGetProperty("index", out var at) && at.TryGetInt32(out var value) ? value : 0;
        var function = call.TryGetProperty("function", out var f) ? f : default;

        if (!_calls.TryGetValue(index, out var building))
        {
            var id = call.TryGetProperty("id", out var identifier) ? identifier.GetString() ?? "" : "";
            var name = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var n)
                ? n.GetString() ?? ""
                : "";
            _calls[index] = building = new Call(id, name);
        }
        else if (function.ValueKind == JsonValueKind.Object
            && function.TryGetProperty("name", out var more)
            && more.GetString() is { Length: > 0 } rest
            && building.FullName.ToString() != rest)
        {
            building.FullName.Append(rest);
        }

        if (function.ValueKind == JsonValueKind.Object
            && function.TryGetProperty("arguments", out var arguments)
            && arguments.ValueKind == JsonValueKind.String)
        {
            building.Arguments.Append(arguments.GetString());
        }
    }

    private IEnumerable<AssistEvent> Flush()
    {
        foreach (var (_, call) in _calls.OrderBy(entry => entry.Key))
        {
            var arguments = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString();
            yield return new AssistEvent.Call(
                new AssistToolCall(call.Id, call.FullName.ToString(), arguments));
        }
        _calls.Clear();
    }

    /// <summary>
    /// Why it stopped. <c>content_filter</c> is this API's refusal: the request
    /// was fine and the provider declined, which is a different thing from an
    /// error and is worth saying differently.
    /// </summary>
    private static AssistStop Translate(string? reason) => reason switch
    {
        "tool_calls" or "function_call" => AssistStop.ToolUse,
        "length" => AssistStop.Length,
        "content_filter" => AssistStop.Refusal,
        "stop" => AssistStop.EndTurn,
        _ => AssistStop.Other,
    };
}
