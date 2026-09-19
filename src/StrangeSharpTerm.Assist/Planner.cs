using System.Text;
using StrangeSharpTerm.Assist.Providers;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// Asks for a plan, and runs none of it.
///
/// The separation is the feature: a fan-out cannot express building a cluster,
/// and what a plan adds is an order, different work per host, and a value
/// carried from one machine to another. All three are things a person should
/// read before a machine acts on them.
/// </summary>
/// <param name="localWorkspace">
/// The folder open on the machine this app is running on, or null -- read-only.
///
/// A plan is usually written from something: the runbook for the cluster, the
/// notes from the last attempt, the manifests it is meant to apply. Those live
/// in the folder the person opened, and a planner that cannot see them writes
/// the plan a package's README would suggest rather than the one they asked for.
/// Reading only: a plan writes nothing, and the run that follows it is where
/// anything changes.
/// </param>
public sealed class Planner(IAssistBackend backend, IWorkspaceAccess? localWorkspace = null)
{
    /// <summary>
    /// The model's reasoning, as it arrives, where the provider offers it.
    ///
    /// Worth more here than anywhere else in the app: deciding which of three
    /// identical servers gets the single-node install is the whole of the work,
    /// and the plan alone shows only which one was chosen. Carries the reasoning
    /// so far rather than the newest piece, so a reader assigns it and is done.
    /// </summary>
    public event EventHandler<string>? Thought;

    /// <summary>
    /// Every plan this planner has been asked for, and every one it wrote.
    ///
    /// A plan is rarely right first time, and the second instruction is almost
    /// always about the first: put OpenClaw on the other one, drop the third
    /// phase, use containerd instead. Without this, each of those is read as a
    /// fresh request by something that has never seen the plan it is being asked
    /// to change.
    /// </summary>
    private readonly List<AssistMessage> _conversation = [];

    /// <summary>How many exchanges are behind the next one. The pane says so.</summary>
    public int Turns => _conversation.Count / 2;

    /// <summary>
    /// What the hosts said, waiting to be carried into the next question.
    ///
    /// Folded into that question rather than sent as a turn of its own: a
    /// conversation alternates, and a report with no answer after it is not a
    /// turn. It also means a run nobody followed up on costs nothing.
    /// </summary>
    private string _reported = "";

    /// <summary>
    /// Tells the planner what happened when its plan ran.
    ///
    /// Without this the planner writes a plan, the hosts carry it out, and the
    /// next question is answered by something that never learned whether any of
    /// it worked -- so "that failed, try something else" is read by the one
    /// participant with no idea what failed.
    /// </summary>
    public void Record(string whatHappened)
    {
        if (whatHappened.Trim().Length == 0)
            return;
        _reported = _reported.Length == 0 ? whatHappened : $"{_reported}\n\n{whatHappened}";
    }

    public async Task<PlanReading> Draft(
        string goal,
        IReadOnlyList<string> hosts,
        CancellationToken cancellationToken = default)
    {
        if (hosts.Count == 0)
            return new PlanReading.Refused("No hosts are selected.");

        var said = new StringBuilder();
        var thought = new StringBuilder();
        var asked = new AssistMessage
        {
            Role = AssistRole.User,
            Text = _reported.Length == 0
                ? goal
                : $"What happened when the last plan ran:\n\n{_reported}\n\n{goal}",
        };

        // This draft's reads, kept out of the conversation: what is remembered
        // is what was asked and the plan that came back, and a later turn that
        // needs a file again can read it again.
        List<AssistMessage> working = [.. _conversation, asked];
        var budget = new CommandBudget(AssistLimits.RunBudget);
        for (var turn = 0; ; turn++)
        {
            said.Clear();
            var calls = new List<AssistToolCall>();
            try
            {
                await foreach (var streamed in backend.Stream(
                    new AssistRequest
                    {
                        // The hosts are named in the system prompt, which is written
                        // fresh each time: what is ticked changes between turns, and
                        // an earlier turn's list must not outlive it.
                        System = Here is { } here
                            ? string.Join("\n\n", AssistPrompts.Planner(hosts), AssistPrompts.PlannerWorkspace(here.RootLabel))
                            : AssistPrompts.Planner(hosts),
                        Messages = [.. working],
                        // None on the last turn, so it has to answer with a plan.
                        Tools = Here is not null && turn < AssistLimits.RunBudget ? WorkspaceTools.ReadingLocal : [],
                    },
                    cancellationToken))
                {
                    switch (streamed)
                    {
                        case AssistEvent.Say say:
                            said.Append(say.Text);
                            break;
                        case AssistEvent.Reasoning reasoning:
                            thought.Append(reasoning.Text);
                            Thought?.Invoke(this, thought.ToString());
                            break;
                        case AssistEvent.Call call:
                            calls.Add(call.Tool);
                            break;
                    }
                }
            }
            catch (AssistException e)
            {
                return new PlanReading.Refused(e.Message);
            }

            if (calls.Count == 0 || turn >= AssistLimits.RunBudget)
                break;

            working.Add(new AssistMessage
            {
                Role = AssistRole.Assistant,
                Text = said.Length == 0 ? null : said.ToString(),
                ToolCalls = calls,
            });

            var results = new List<AssistToolResult>();
            foreach (var call in calls)
                results.Add(await Look(call, budget, cancellationToken));
            working.Add(new AssistMessage { Role = AssistRole.User, ToolResults = results });
        }

        var answer = said.ToString();
        var reading = RunPlan.Read(answer, hosts);

        // Kept whatever it says, refusals included. A plan refused for naming a
        // host nobody selected is exactly the turn the next one needs to see, or
        // it will write the same thing again.
        _conversation.Add(asked);
        _conversation.Add(new AssistMessage { Role = AssistRole.Assistant, Text = answer });
        // Carried, so it is not carried twice. It is in the history now.
        _reported = "";
        return reading;
    }

    /// <inheritdoc cref="FleetAgent"/>
    private IWorkspaceAccess? Here => localWorkspace is { Root.Length: > 0 } open ? open : null;

    /// <summary>
    /// A file read while drafting, on this machine, as the pane shows it.
    ///
    /// Raised so the pane can say what the planner is reading: a read nobody
    /// sees is a plan whose sources nobody can check.
    /// </summary>
    public event EventHandler<TranscriptEntry.Step>? Looked;

    /// <summary>
    /// One read, through the same <see cref="WorkspaceCalls"/> every other
    /// assistant uses -- the policy, the budget and the redaction are the same.
    /// Anything but reading this machine's folder is turned down before it gets
    /// there, since reading is all a planner was offered.
    /// </summary>
    private async Task<AssistToolResult> Look(
        AssistToolCall call,
        ICommandBudget budget,
        CancellationToken cancellationToken)
    {
        if (call.Name is not (WorkspaceTools.ListLocalFiles or WorkspaceTools.ReadLocalFile)
            || Here is not { } here)
        {
            return new AssistToolResult(
                call.Id,
                "While planning you can only read the folder on the user's own machine. Put anything "
                    + "else in the plan as a command.",
                Failed: true);
        }

        var calls = new WorkspaceCalls(
            here,
            // Never asked: reads do not stop at a gate, and nothing else gets here.
            new StandingAnswer(false),
            "this machine",
            step => Looked?.Invoke(this, (TranscriptEntry.Step)step),
            step => Looked?.Invoke(this, (TranscriptEntry.Step)step));

        return (await calls.Carry(call, budget, cancellationToken)).Result;
    }
}

/// <summary>
/// How settings become a backend.
///
/// The only place in the app that maps a choice to a provider, so adding a third
/// one is a case here and nothing else.
/// </summary>
public static class AssistBackends
{
    /// <summary>
    /// A backend for these settings, or null when there is no key for the chosen
    /// provider.
    ///
    /// Null rather than an exception: no key is an ordinary state on a fresh
    /// install, and what it deserves is a sentence in the pane offering Settings,
    /// not a failure at the moment someone asks a question.
    /// </summary>
    public static IAssistBackend? For(AssistSettings settings, ISecretStore? secrets = null)
    {
        var provider = settings.Backend;
        if (AssistKeys.Key(provider, secrets) is not { Length: > 0 } key)
            return null;

        return provider.Id switch
        {
            AssistProviderId.DeepSeek => DeepSeekBackend.Create(key, settings.ModelName, settings.Endpoint),
            _ => ClaudeBackend.Create(key, settings.ModelName, settings.Endpoint),
        };
    }

    /// <summary>What a pane says when there is no key, naming the provider it needs one for.</summary>
    public static string NoKey(AssistSettings settings) =>
        $"No API key for {settings.Backend.Name}. Add one in Settings, or set {settings.Backend.KeyVariable}.";
}
