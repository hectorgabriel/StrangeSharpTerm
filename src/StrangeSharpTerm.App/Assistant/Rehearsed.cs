using System.Runtime.CompilerServices;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Assistant;

/// <summary>
/// A provider that says what it was told to say, and a host that answers from a
/// script.
///
/// These are here rather than in the test project because what they exist for is
/// the <c>--demo-*</c> flags: the assistant and orchestrator panes only exist
/// after a real connection and a real API key, and neither is available to
/// whoever is looking at the window. Every UI change so far has found something
/// that every test passed through, so the panes have to be lookable at.
///
/// Nothing here reaches a network or a server.
/// </summary>
internal sealed class RehearsedBackend(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
{
    private int _turn;

    public string ProviderName => AssistProvider.Claude.Name;

    public string Model => AssistProvider.Claude.DefaultModel;

    internal static IReadOnlyList<AssistEvent> Says(string text) =>
    [
        new AssistEvent.Say(text),
        new AssistEvent.Finished(AssistStop.EndTurn),
    ];

    internal static IReadOnlyList<AssistEvent> Runs(string command, string why, string id) =>
    [
        new AssistEvent.Call(new AssistToolCall(
            id,
            AssistTools.RunCommand,
            System.Text.Json.JsonSerializer.Serialize(new { command, why }))),
        new AssistEvent.Finished(AssistStop.ToolUse),
    ];

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = _turn < turns.Length ? turns[_turn] : Says("That is all I have.");
        _turn++;

        foreach (var streamed in turn)
        {
            // Slowly enough to see it arrive, because how a pane looks while it
            // is answering is half of what there is to look at.
            await Task.Delay(120, cancellationToken);
            yield return streamed;
        }
    }
}

/// <inheritdoc cref="RehearsedBackend"/>
internal sealed class RehearsedHost(string alias, ServerMetrics metrics, string tail) : IHostAccess
{
    public string Alias => alias;

    public Task<HostSnapshot> Look(
        bool wantsMetrics,
        bool wantsTail,
        int tailLines,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostSnapshot(
            "Linux 6.1.0-18-amd64 x86_64",
            wantsMetrics ? metrics : null,
            wantsTail ? tail : null));

    public async Task<CommandOutcome> Run(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(250, cancellationToken);
        return new CommandOutcome(0, Answers.GetValueOrDefault(command, "(no output)"));
    }

    internal Dictionary<string, string> Answers { get; } = [];
}
