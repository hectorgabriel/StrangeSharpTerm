namespace StrangeSharpTerm.Assist;

/// <summary>Who is speaking in a transcript sent to a provider.</summary>
public enum AssistRole
{
    User,
    Assistant,
}

/// <summary>A command the model asked for, as the wire carries it.</summary>
/// <param name="Arguments">Raw JSON, because a provider streams it as text and only the caller knows the schema.</param>
public sealed record AssistToolCall(string Id, string Name, string Arguments);

/// <summary>What a tool call produced, on its way back to the model.</summary>
public sealed record AssistToolResult(string CallId, string Output, bool Failed = false);

/// <summary>
/// One turn, in the shape both providers can carry.
///
/// Not either provider's own message type: Claude puts tool results in content
/// blocks on a user message and DeepSeek puts them in messages of their own, and
/// the agent loop above should not know which.
/// </summary>
public sealed record AssistMessage
{
    public required AssistRole Role { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<AssistToolCall> ToolCalls { get; init; } = [];

    /// <summary>Results for the previous assistant turn's calls. Only ever on a user message.</summary>
    public IReadOnlyList<AssistToolResult> ToolResults { get; init; } = [];

    /// <summary>
    /// Roughly how much of a request this message is, in characters.
    ///
    /// Characters rather than tokens, which would mean a tokeniser per
    /// provider to answer a question that does not need that much precision:
    /// what this is for is keeping a conversation from growing without limit,
    /// and four characters to the token is close enough to set a budget by.
    /// It counts what is actually sent, which is mostly command output.
    /// </summary>
    public int Size =>
        (Text?.Length ?? 0)
        + ToolCalls.Sum(call => call.Name.Length + call.Arguments.Length)
        + ToolResults.Sum(result => result.Output.Length);
}

/// <summary>A tool offered to the model. There is exactly one, and see <see cref="AssistTools"/> for why.</summary>
public sealed record AssistTool(string Name, string Description, string JsonSchema);

public sealed record AssistRequest
{
    public required string System { get; init; }

    public required IReadOnlyList<AssistMessage> Messages { get; init; }

    public IReadOnlyList<AssistTool> Tools { get; init; } = [];
}

/// <summary>Why the model stopped talking.</summary>
public enum AssistStop
{
    /// <summary>It finished its answer.</summary>
    EndTurn,

    /// <summary>It wants a tool run before it says more.</summary>
    ToolUse,

    /// <summary>It ran out of room. The answer is a fragment.</summary>
    Length,

    /// <summary>The provider declined server-side. Not the same as the model saying no.</summary>
    Refusal,

    Other,
}

/// <summary>
/// What arrives while a provider is answering.
///
/// A stream rather than a finished message because a pane that sits blank for
/// forty seconds looks broken, and because the reasoning is worth showing as it
/// happens or not at all.
/// </summary>
public abstract record AssistEvent
{
    private AssistEvent() { }

    /// <summary>Summarised reasoning, where the provider offers it.</summary>
    public sealed record Reasoning(string Text) : AssistEvent;

    /// <summary>A piece of the answer.</summary>
    public sealed record Say(string Text) : AssistEvent;

    /// <summary>A complete tool call. Emitted whole: half a JSON argument is no use to anyone.</summary>
    public sealed record Call(AssistToolCall Tool) : AssistEvent;

    public sealed record Finished(AssistStop Reason) : AssistEvent;
}

/// <summary>
/// The one seam between everything above the wire and the provider underneath it.
///
/// The transcript, the redaction, the context block, the gate and staging a
/// command into a terminal are all provider-independent, and stay that way as
/// long as this is the only place a provider is named.
/// </summary>
public interface IAssistBackend
{
    /// <summary>As the pane header says it: "Claude", "DeepSeek".</summary>
    string ProviderName { get; }

    /// <summary>The model answering, which the header names beside the provider.</summary>
    string Model { get; }

    IAsyncEnumerable<AssistEvent> Stream(AssistRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The assistant could not reach its provider, or was refused by it. The message is fit to show a person.</summary>
public sealed class AssistException(string message, Exception? inner = null) : Exception(message, inner);
