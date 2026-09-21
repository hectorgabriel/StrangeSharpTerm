namespace StrangeSharpTerm.Assist;

/// <summary>How a host came out of a run.</summary>
public enum HostOutcome
{
    /// <summary>It was asked and it answered.</summary>
    Reported,

    /// <summary>It was asked and something went wrong.</summary>
    Failed,

    /// <summary>
    /// It was not asked. Almost always because it is not connected: connecting
    /// can raise a host-key decision, and a fan-out that stopped on a dialog per
    /// host would be worse than one that says plainly which hosts it left out.
    /// </summary>
    NotAsked,

    /// <summary>
    /// A person stopped the run before it finished with this host.
    ///
    /// Its own outcome rather than one of the three above, because it is none of
    /// them: it did not fail, it was not skipped, and whatever it had found by
    /// then is not an answer to the question. Folding it into "failed" accused
    /// every host in the run of breaking whenever somebody pressed Stop.
    /// </summary>
    Stopped,
}

/// <summary>What one host contributed.</summary>
public sealed record HostFinding(string Alias, HostOutcome Outcome, string Text, int CommandsRun = 0)
{
    /// <summary>
    /// What a host's answer amounts to, in the words its row shows.
    ///
    /// Here rather than in each caller because there are two of them -- a
    /// fan-out and a planned run -- and they disagreed: the same stopped host
    /// read as "failed" in one and "not asked" in the other.
    ///
    /// A stopped host says only that. Whatever it had said before the
    /// interruption is in its transcript, where the exchange can be opened; it
    /// is not an answer to the question, and putting it in the row would read
    /// like one.
    /// </summary>
    /// <summary>
    /// A host that answered and ran none of what it was given to run.
    ///
    /// Its own case because it is the one that looks like success: a
    /// confident "I have installed nginx" with nothing having reached the
    /// server. Recorded as a failure, with what it said kept so a person can see
    /// why.
    /// </summary>
    public static HostFinding RanNone(string alias, AgentAnswer answer) => new(
        alias,
        HostOutcome.Failed,
        answer.Text.Length > 0
            ? $"It ran none of the commands. It said: {answer.Text}"
            : "It ran none of the commands.",
        0)
    {
        RanNothing = true,
    };

    /// <summary>Whether this host was given commands and ran none of them.</summary>
    public bool RanNothing { get; init; }

    public static HostFinding From(string alias, AgentAnswer answer) => answer switch
    {
        { Stopped: true } => new HostFinding(alias, HostOutcome.Stopped, "Stopped.", answer.CommandsRun),
        { Failed: true } => new HostFinding(
            alias, HostOutcome.Failed, answer.Failure ?? "It did not answer.", answer.CommandsRun),
        { Text.Length: 0 } => new HostFinding(
            alias, HostOutcome.Failed, "It returned nothing.", answer.CommandsRun),
        _ => new HostFinding(alias, HostOutcome.Reported, answer.Text, answer.CommandsRun),
    };

    /// <summary>The word the row shows on the right: "reported", "failed", "stopped", "not asked".</summary>
    public string Label => Outcome switch
    {
        HostOutcome.Reported => "reported",
        HostOutcome.Failed => "failed",
        HostOutcome.Stopped => "stopped",
        _ => "not asked",
    };
}

