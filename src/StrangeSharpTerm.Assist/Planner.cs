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

    public async Task<PlanReading> Draft(
        string goal,
        IReadOnlyList<string> hosts,
        CancellationToken cancellationToken = default)
    {
        if (hosts.Count == 0)
            return new PlanReading.Refused("No hosts are selected.");

        var said = new StringBuilder();
        var thought = new StringBuilder();
        try
        {
            await foreach (var streamed in backend.Stream(
                new AssistRequest
                {
                    System = AssistPrompts.Planner(hosts),
                    Messages = [new AssistMessage { Role = AssistRole.User, Text = goal }],
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

        return RunPlan.Read(said.ToString(), hosts);
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
