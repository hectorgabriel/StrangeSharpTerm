namespace StrangeSharpTerm.Assist;

/// <summary>
/// How the assistant is configured, across every pane.
///
/// Both context switches are here because both are choices about what leaves the
/// machine, and a choice like that belongs somewhere a person can find it rather
/// than in a pane that happens to be open.
/// </summary>
public sealed record AssistSettings
{
    public AssistProviderId Provider { get; init; } = AssistProviderId.Claude;

    /// <summary>The model, by name. Null means whatever the provider's default is today.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// A different endpoint to talk to. Null is the provider's own.
    ///
    /// This is what a provider's wire format is checked against a stub with,
    /// which is the only way to exercise the whole path without an account.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>Whether a question carries what the terminal beside it is showing.</summary>
    public bool SendTerminalTail { get; init; } = true;

    /// <summary>Whether a question carries the host's current metrics.</summary>
    public bool SendMetrics { get; init; } = true;

    /// <summary>How much of the terminal goes. The Swift app's figure.</summary>
    public int TerminalTailLines { get; init; } = 120;

    /// <summary>
    /// Whether the pane may run commands at all. Off by default, and the pane
    /// header is where it is turned on -- for one conversation, not for the app.
    /// </summary>
    public bool AllowCommandsByDefault { get; init; }

    public AssistProvider Backend => AssistProvider.For(Provider);

    /// <summary>The model actually asked for.</summary>
    public string ModelName => Model is { Length: > 0 } chosen ? chosen : Backend.DefaultModel;

    /// <summary>
    /// The environment's say in this, read once when the app starts.
    ///
    /// Three variables, as the Swift app had them: the pair that redirects a
    /// development build at a stub is the only way the wire format gets
    /// exercised without an account.
    /// </summary>
    public AssistSettings WithEnvironmentOverrides()
    {
        var settings = this;

        if (Environment.GetEnvironmentVariable("STRANGESHARPTERM_ASSIST_PROVIDER") is { Length: > 0 } provider
            && Enum.TryParse<AssistProviderId>(provider, ignoreCase: true, out var parsed))
        {
            settings = settings with { Provider = parsed };
        }

        if (Environment.GetEnvironmentVariable("STRANGESHARPTERM_ASSIST_MODEL") is { Length: > 0 } model)
            settings = settings with { Model = model };

        if (Environment.GetEnvironmentVariable("STRANGESHARPTERM_ASSIST_ENDPOINT") is { Length: > 0 } endpoint)
            settings = settings with { Endpoint = endpoint };

        return settings;
    }
}

/// <summary>
/// The bounds every run is held inside.
///
/// Numbers rather than settings on purpose: each of them is a promise the design
/// makes about what a pane can do to a server, and a promise a user can raise is
/// not one.
/// </summary>
public static class AssistLimits
{
    /// <summary>
    /// Commands per question, after which the model is told to summarise rather
    /// than being stopped mid-investigation.
    /// </summary>
    public const int CommandBudget = 12;

    /// <summary>
    /// Commands for a whole orchestrated run, on top of each host's own budget.
    /// A fan-out multiplies everything, this included.
    /// </summary>
    public const int RunBudget = 60;

    /// <summary>
    /// Hosts asked at once. Not one, or a run over a rack takes as long as the
    /// sum of its hosts; not all of them, because every host in flight is
    /// another gate that can stop for a person, and a queue of eight approvals
    /// is a queue nobody reads.
    /// </summary>
    public const int Concurrency = 3;

    /// <summary>
    /// How long the pane waits for one command. The command is not killed -- the
    /// runner cannot be interrupted -- so what this bounds is the waiting.
    /// </summary>
    public static TimeSpan CommandTimeout { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The same, inside a planned run. <c>apt install</c> and <c>kubeadm init</c>
    /// legitimately take this long, and a loop that gave up after a minute would
    /// carry on reading the output of a command it had stopped waiting for.
    /// </summary>
    public static TimeSpan PlanCommandTimeout { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Phases in a plan. More than this is not a plan anyone will read.</summary>
    public const int MaxPhases = 12;

    /// <summary>
    /// How much of a command's output goes back to the model. Enough for a
    /// listing or a stack trace; not a whole log file.
    /// </summary>
    public const int MaxOutputCharacters = 8000;
}
