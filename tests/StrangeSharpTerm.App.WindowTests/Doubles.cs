using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// A host that answers nothing, and a provider that says what it was told to.
///
/// Shared by the tests that need no more than this. <see cref="AssistantPaneTests"/>
/// keeps its own richer pair under the same names -- one that records what it
/// was asked and one that can be told to call a tool -- because what those
/// tests are about is the asking. A nested type wins inside its own class, so
/// the two do not collide.
/// </summary>
internal sealed class Quiet(string alias) : IHostAccess
{
    public string Alias => alias;

    public Task<HostSnapshot> Look(bool metrics, bool tail, int lines, CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostSnapshot());

    public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommandOutcome(0, ""));
}

/// <summary>A provider that says what it was told to, one scripted turn per call.</summary>
internal sealed class Canned(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
{
    private int _turn;

    public string ProviderName => "Canned";

    public string Model => "canned-1";

    public static IReadOnlyList<AssistEvent> Says(string text) =>
        [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

    public static IReadOnlyList<AssistEvent> Runs(string command, string why = "because", string id = "call_1") =>
    [
        new AssistEvent.Call(new AssistToolCall(
            id, AssistTools.RunCommand, System.Text.Json.JsonSerializer.Serialize(new { command, why }))),
        new AssistEvent.Finished(AssistStop.ToolUse),
    ];

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = _turn < turns.Length ? turns[_turn] : Says("Nothing more.");
        _turn++;
        foreach (var streamed in turn)
        {
            await Task.Yield();
            yield return streamed;
        }
    }
}
