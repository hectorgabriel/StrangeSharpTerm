using System.Runtime.CompilerServices;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// What a pane has spent, across every request made on its behalf.
///
/// Per pane rather than per agent, because a pane is what a person is looking
/// at and an agent is not: the orchestrator alone drives a planner, a fleet
/// assistant and a conversation for each host in a plan, and the question
/// worth answering is what the window cost, not which object asked.
///
/// Not reset by /clear. Clearing forgets a conversation; what it cost was
/// still spent.
/// </summary>
public sealed class UsageMeter
{
    private readonly Lock _lock = new();

    public AssistUsage Total { get; private set; } = AssistUsage.None;

    /// <summary>The total changed. Raised on whichever thread the request finished on.</summary>
    public event EventHandler<AssistUsage>? Changed;

    public void Add(AssistUsage usage)
    {
        AssistUsage total;
        lock (_lock)
            total = Total += usage;
        Changed?.Invoke(this, total);
    }
}

/// <summary>
/// A backend that tells a <see cref="UsageMeter"/> what each request cost.
///
/// A wrapper rather than something each agent does, so that nothing above the
/// seam has to remember to count: whatever is built on this backend is counted,
/// including agents nobody has written yet.
/// </summary>
public sealed class MeteredBackend(IAssistBackend inner, UsageMeter meter) : IAssistBackend
{
    public string ProviderName => inner.ProviderName;

    public string Model => inner.Model;

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamed in inner.Stream(request, cancellationToken))
        {
            if (streamed is AssistEvent.Spent spent)
                meter.Add(spent.Usage);
            yield return streamed;
        }
    }
}
