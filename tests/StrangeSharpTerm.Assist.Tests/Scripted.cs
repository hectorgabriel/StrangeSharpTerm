using System.Runtime.CompilerServices;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// A provider that says what it was told to say, one scripted turn per call.
///
/// The loop is what these tests are about -- the gate, the budget, the
/// redaction, the way a refusal is reported back -- and none of that should need
/// a network to exercise.
/// </summary>
internal sealed class ScriptedBackend(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
{
    private int _turn;

    public string ProviderName => "Scripted";

    public string Model => "scripted-1";

    /// <summary>Every request this backend was handed, in order.</summary>
    internal List<AssistRequest> Requests { get; } = [];

    /// <summary>Set to throw instead of answering, which is how a provider failure is tested.</summary>
    internal AssistException? Fails { get; set; }

    internal static IReadOnlyList<AssistEvent> Says(string text) =>
        [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

    internal static IReadOnlyList<AssistEvent> Runs(string command, string why = "because", string id = "call_1") =>
    [
        new AssistEvent.Call(new AssistToolCall(
            id,
            AssistTools.RunCommand,
            System.Text.Json.JsonSerializer.Serialize(new { command, why }))),
        new AssistEvent.Finished(AssistStop.ToolUse),
    ];

    internal static IReadOnlyList<AssistEvent> Calls(string tool, object arguments, string id = "call_1") =>
    [
        new AssistEvent.Call(new AssistToolCall(
            id, tool, System.Text.Json.JsonSerializer.Serialize(arguments))),
        new AssistEvent.Finished(AssistStop.ToolUse),
    ];

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Fails is { } failure)
            throw failure;

        // A script that runs out keeps answering, so a loop that went further
        // than expected fails on its assertion rather than on an index.
        var turn = _turn < turns.Length ? turns[_turn] : Says("Nothing more to add.");
        _turn++;

        foreach (var streamed in turn)
        {
            await Task.Yield();
            yield return streamed;
        }
    }
}

/// <summary>A host that answers from a script rather than over ssh.</summary>
internal sealed class FakeHost(string alias) : IHostAccess
{
    public string Alias => alias;

    internal HostSnapshot Snapshot { get; set; } = new();

    internal Func<string, CommandOutcome> Answer { get; set; } = _ => new CommandOutcome(0, "");

    /// <summary>Every command that actually reached the host, in order.</summary>
    internal List<string> Ran { get; } = [];

    /// <summary>What Look was asked for, so the settings switches can be checked.</summary>
    internal (bool Metrics, bool Tail, int Lines)? Asked { get; private set; }

    internal bool Throws { get; set; }

    public Task<HostSnapshot> Look(bool metrics, bool terminalTail, int tailLines, CancellationToken cancellationToken = default)
    {
        Asked = (metrics, terminalTail, tailLines);
        return Throws ? throw new IOException("the host went away") : Task.FromResult(Snapshot);
    }

    public Task<CommandOutcome> Run(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Ran.Add(command);
        Timeouts.Add(timeout);
        return Task.FromResult(Answer(command));
    }

    /// <summary>The timeout each command was given, which a planned run doubles up on purpose.</summary>
    internal List<TimeSpan> Timeouts { get; } = [];
}

/// <summary>A gate that remembers what it was asked and answers to order.</summary>
internal sealed class RecordingGate(bool answer = true) : ICommandGate
{
    internal List<PendingCommand> Asked { get; } = [];

    internal Func<PendingCommand, bool> Answer { get; set; } = _ => answer;

    public Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default)
    {
        Asked.Add(command);
        return Task.FromResult(Answer(command));
    }
}

internal static class Fixtures
{
    internal static AssistSettings Settings { get; } = new();

    internal static ServerMetrics Metrics { get; } = new()
    {
        Uptime = "10 days",
        LoadAverages = [1.0, 1.1, 1.2],
        DiskUsedBytes = 49_000_000_000,
        DiskTotalBytes = 52_000_000_000,
    };
}

/// <summary>A backend that says when it starts and finishes, for counting what is in flight.</summary>
internal sealed class WatchingBackend(Action started, Action finished) : IAssistBackend
{
    public string ProviderName => "Watching";

    public string Model => "watching-1";

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started();
        // Long enough that the next host would overlap if nothing held it back.
        await Task.Delay(30, cancellationToken);
        yield return new AssistEvent.Say("Done.");
        yield return new AssistEvent.Finished(AssistStop.EndTurn);
        finished();
    }
}

/// <summary>
/// A backend that keeps the instruction it was given, without the context block
/// in front of it. What a planned run sends each host is the thing being checked.
/// </summary>
internal sealed class RecordingBackend(List<string> asked, string answer) : IAssistBackend
{
    public string ProviderName => "Recording";

    public string Model => "recording-1";

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sent = request.Messages.Last().Text ?? "";
        // The context block is prepended to every question; the instruction is
        // what follows it.
        var instruction = sent.StartsWith("Host: ", StringComparison.Ordinal)
            ? string.Join("\n\n", sent.Split("\n\n").Skip(1))
            : sent;
        lock (asked)
            asked.Add(instruction.Trim());

        await Task.Yield();
        yield return new AssistEvent.Say(answer);
        yield return new AssistEvent.Finished(AssistStop.EndTurn);
    }
}

/// <summary>
/// A workspace held in a dictionary.
///
/// The agent's file tools are a gate, a budget and a policy, and none of those
/// needs SFTP: what is being checked is what stops, what does not, and what goes
/// back to the model when something is refused.
/// </summary>
internal sealed class FakeWorkspace(string root = "/home/ops/srv/app") : IWorkspaceAccess
{
    private readonly Dictionary<string, string> _files = [];

    public string Root { get; set; } = root;

    public string RootLabel => Root.Length == 0 ? "" : "~/srv/app";

    /// <summary>Every path actually written, with what landed there.</summary>
    internal Dictionary<string, string> Written { get; } = [];

    internal FakeWorkspace With(string relative, string content)
    {
        _files[relative] = content;
        return this;
    }

    public Task<string> List(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Join('\n', _files.Keys.Order()));

    public Task<FileText> Read(string path, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(path, out var content)
            ? Task.FromResult(new FileText(
                PosixPath.Join(Root, path), path, content, "\n", false, false, content.Length, new DateTime(2026, 9, 2)))
            : Task.FromException<FileText>(new FileNotFoundException($"There is no {path} in this workspace."));

    public Task<FileChange> Plan(string path, string text, CancellationToken cancellationToken = default)
    {
        var exists = _files.TryGetValue(path, out var before);
        return Task.FromResult(new FileChange(
            PosixPath.Join(Root, path),
            path,
            text,
            !exists,
            Diff.Between(before ?? "", text)));
    }

    public Task Write(FileChange change, CancellationToken cancellationToken = default)
    {
        _files[change.Relative] = change.Text;
        Written[change.Relative] = change.Text;
        return Task.CompletedTask;
    }
}
