namespace StrangeSharpTerm.Assist;

/// <summary>What a person decided about one tool call.</summary>
public enum ToolApproval
{
    /// <summary>No, this time and by default.</summary>
    No,

    /// <summary>Yes, this once. The next call asks again.</summary>
    Once,

    /// <summary>Yes, and stop asking for this tool. The only way a standing pass is ever granted.</summary>
    Always,
}

/// <summary>
/// One call from a connected tool, waiting on a person.
/// </summary>
/// <param name="Destination">
/// Where the call goes. Named rather than only the tool, because the arguments
/// go to whichever server owns it — a second destination, and a different one
/// from the provider.
/// </param>
/// <param name="Arguments">
/// The exact arguments, pretty-printed, shown in the bar itself rather than
/// behind a disclosure: a gate whose substance is one click away is a gate
/// people approve without reading.
/// </param>
/// <param name="ReadOnlyClaim">
/// The server says this tool only reads. Shown and never acted on — it is a
/// claim by the party being trusted.
/// </param>
/// <param name="MayBeGranted">
/// Whether Always allow may be offered at all. A tool the server itself calls
/// destructive cannot be given a standing pass.
/// </param>
public sealed record PendingToolCall(
    string Host,
    string Server,
    string Tool,
    string Destination,
    string Arguments,
    bool ReadOnlyClaim,
    bool MayBeGranted);

/// <summary>What a tool call produced.</summary>
public sealed record ToolReply(string Output, bool Failed = false);

/// <summary>
/// Tools that are not this app's own.
///
/// The seam connected tool servers arrive through. Everything the agent loop
/// does with them — offering them to a provider, stopping at the gate, redacting
/// what comes back, spending the same twelve-step budget — is the same whether
/// there is one of these or none.
/// </summary>
public interface IExternalTools
{
    /// <summary>What to offer the provider, already namespaced by whoever owns them.</summary>
    IReadOnlyList<AssistTool> Offered { get; }

    /// <summary>Whether this name belongs here at all.</summary>
    bool Owns(string qualifiedName);

    /// <summary>What a person needs to see before this call goes out.</summary>
    PendingToolCall Describe(string host, string qualifiedName, string argumentsJson);

    /// <summary>Whether a person has already granted this tool a standing pass.</summary>
    bool MayRunUnattended(string qualifiedName);

    /// <summary>Records a standing pass. Called only when a person pressed Always allow.</summary>
    void Grant(string qualifiedName);

    Task<ToolReply> Call(string qualifiedName, string argumentsJson, CancellationToken cancellationToken = default);
}
