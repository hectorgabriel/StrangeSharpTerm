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
public sealed class Planner(IAssistBackend backend)
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

    public async Task<PlanReading> Draft(
        string goal,
        IReadOnlyList<string> hosts,
        CancellationToken cancellationToken = default)
    {
        if (hosts.Count == 0)
            return new PlanReading.Refused("No hosts are selected.");

        var said = new StringBuilder();
        var thought = new StringBuilder();
        var asked = new AssistMessage { Role = AssistRole.User, Text = goal };
        try
        {
            await foreach (var streamed in backend.Stream(
                new AssistRequest
                {
                    // The hosts are named in the system prompt, which is written
                    // fresh each time: what is ticked changes between turns, and
                    // an earlier turn's list must not outlive it.
                    System = AssistPrompts.Planner(hosts),
                    Messages = [.. _conversation, asked],
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
                }
            }
        }
        catch (AssistException e)
        {
            return new PlanReading.Refused(e.Message);
        }

        var answer = said.ToString();
        var reading = RunPlan.Read(answer, hosts);

        // Kept whatever it says, refusals included. A plan refused for naming a
        // host nobody selected is exactly the turn the next one needs to see, or
        // it will write the same thing again.
        _conversation.Add(asked);
        _conversation.Add(new AssistMessage { Role = AssistRole.Assistant, Text = answer });
        return reading;
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
