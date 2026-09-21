using System.Text.Json;
using System.Text.RegularExpressions;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// One phase of a plan: an order, the hosts it applies to, the commands each of
/// them will be given, and at most one value it yields.
/// </summary>
public sealed record PlanPhase
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Hosts { get; init; }

    /// <summary>
    /// Why this phase is these commands on these hosts, in one line, or null.
    ///
    /// A plan that says what without why is one a person can only check for
    /// syntax. Which of three identical servers gets the single-node
    /// installation is a decision, and the reason for it belongs beside the
    /// phase rather than in reasoning that scrolls away.
    /// </summary>
    public string? Why { get; init; }

    /// <summary>
    /// The commands, in order, that each of this phase's hosts will be asked to
    /// run.
    ///
    /// Named here rather than left to each host's assistant at run time: the
    /// point of a plan is to be read before anything happens, and "install
    /// Kubernetes" is not something anyone can check. They are still only asked
    /// for -- every one of them meets <see cref="CommandPolicy"/> and the gate
    /// when the phase runs, exactly as an unplanned command does.
    /// </summary>
    public required IReadOnlyList<string> Commands { get; init; }

    /// <summary>
    /// The one value this phase produces, by name, or null. Later phases refer
    /// to it as <c>{{name}}</c>.
    /// </summary>
    public string? Capture { get; init; }

    /// <summary>
    /// Whether it will run. A plan is shown in full and phases can be switched
    /// off, because a plan summarised into "3 phases, 4 hosts" would be a plan
    /// nobody could review.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The <c>{{name}}</c> references in this phase's commands.</summary>
    public IReadOnlyList<string> Placeholders => RunPlan.PlaceholdersIn(Commands);
}

/// <summary>A plan, as written and before anything has run.</summary>
public sealed partial record RunPlan(IReadOnlyList<PlanPhase> Phases)
{
    /// <summary>What the header says beside the name.</summary>
    public string Summary => $"{Phases.Count} phase{(Phases.Count == 1 ? "" : "s")}";

    /// <summary>
    /// Reads a plan, or says why it will not be shown.
    ///
    /// A plan that would not be safe to run is refused rather than displayed,
    /// and the refusal names the phase and the reason: showing an unsafe plan
    /// with a Run button beneath it puts the whole weight of the check on
    /// somebody reading carefully.
    /// </summary>
    public static PlanReading Read(string? answer, IReadOnlyList<string> selectedHosts)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return new PlanReading.Refused("The planner returned nothing.");

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(Unfence(answer)).RootElement;
        }
        catch (JsonException e)
        {
            return new PlanReading.Refused($"The plan was not readable. ({e.Message})");
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("phases", out var phases)
            || phases.ValueKind != JsonValueKind.Array)
        {
            return new PlanReading.Refused("The plan had no phases in it.");
        }

        List<PlanPhase> read = [];
        foreach (var phase in phases.EnumerateArray())
        {
            read.Add(new PlanPhase
            {
                Name = Text(phase, "name") is { Length: > 0 } name ? name : $"Phase {read.Count + 1}",
                Hosts = phase.TryGetProperty("hosts", out var hosts) && hosts.ValueKind == JsonValueKind.Array
                    ? [.. hosts.EnumerateArray().Select(h => h.GetString() ?? "").Where(h => h.Length > 0)]
                    : [],
                Why = Text(phase, "why") is { Length: > 0 } why ? why : null,
                Commands = phase.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array
                    ? [.. commands.EnumerateArray()
                        .Select(command => (command.GetString() ?? "").Trim())
                        .Where(command => command.Length > 0)]
                    : [],
                Capture = Text(phase, "capture") is { Length: > 0 } capture ? capture : null,
            });
        }

        var plan = new RunPlan(read);
        return Check(plan, selectedHosts) is { } refusal
            ? new PlanReading.Refused(refusal)
            : new PlanReading.Ok(plan);
    }

    /// <summary>
    /// Every reason a plan is refused, each naming its phase.
    ///
    /// Null when there is none.
    /// </summary>
    internal static string? Check(RunPlan plan, IReadOnlyList<string> selectedHosts)
    {
        if (plan.Phases.Count == 0)
            return "The plan had no phases in it.";

        if (plan.Phases.Count > AssistLimits.MaxPhases)
            return $"The plan has {plan.Phases.Count} phases, and more than {AssistLimits.MaxPhases} is not a plan anyone will read.";

        var selected = new HashSet<string>(selectedHosts, StringComparer.Ordinal);
        var produced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (phase, number) in plan.Phases.Select((phase, index) => (phase, index + 1)))
        {
            if (phase.Hosts.Count == 0)
                return $"Phase {number}, {phase.Name}, names no hosts.";

            if (phase.Commands.Count == 0)
                return $"Phase {number}, {phase.Name}, names no commands for its hosts to run.";

            // Refused here rather than when it runs: a phase runs its commands
            // as they were approved, so a sudo in a shape that cannot be given
            // the password is a command that was never going to work.
            if (phase.Commands.FirstOrDefault(command => Sudo.Mentions(command) && Sudo.Prepared(command) is null) is { } sudo)
                return $"Phase {number}, {phase.Name}, runs \"{sudo}\". {Sudo.NotGiven}";

            if (phase.Hosts.FirstOrDefault(host => !selected.Contains(host)) is { } stranger)
                return $"Phase {number}, {phase.Name}, names {stranger}, which nobody selected.";

            // A placeholder is checked before the phase that produces it is
            // recorded, so a phase cannot refer to its own capture.
            if (phase.Placeholders.FirstOrDefault(name => !produced.Contains(name)) is { } unknown)
                return $"Phase {number}, {phase.Name}, uses {{{{{unknown}}}}}, which no earlier phase produces.";

            if (phase.Capture is { Length: > 0 } capture)
            {
                if (phase.Hosts.Count > 1)
                    return $"Phase {number}, {phase.Name}, captures {capture} on {phase.Hosts.Count} hosts at once, so there would be no single value to carry.";
                produced.Add(capture);
            }
        }

        return null;
    }

    /// <summary>The <c>{{name}}</c> references in a piece of text, in order and without repeats.</summary>
    internal static IReadOnlyList<string> PlaceholdersIn(string? text) =>
        text is null
            ? []
            : [.. Placeholder().Matches(text).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>The same, across every command in a phase, in the order they first appear.</summary>
    internal static IReadOnlyList<string> PlaceholdersIn(IReadOnlyList<string> commands) =>
        [.. commands.SelectMany(PlaceholdersIn).Distinct(StringComparer.Ordinal)];

    /// <summary>Puts captured values into a command. A name with no value is left alone, and the caller refuses the phase.</summary>
    internal static string Fill(string task, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(task, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

    /// <summary>
    /// A model asked for JSON often sends it inside a fence anyway. Taking the
    /// fence off is cheaper than a second round trip asking it not to.
    /// </summary>
    internal static string Unfence(string answer)
    {
        var text = answer.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0)
            return text;
        var body = text[(firstBreak + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing < 0 ? body : body[..closing]).Trim();
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>A plan, or the reason there is not one.</summary>
public abstract record PlanReading
{
    private PlanReading() { }

    public sealed record Ok(RunPlan Plan) : PlanReading;

    /// <summary>The refusal, naming which phase and why.</summary>
    public sealed record Refused(string Reason) : PlanReading;
}
