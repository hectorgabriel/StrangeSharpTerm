using System.Text;
using System.Text.Json;

namespace StrangeSharpTerm.Assist;

/// <summary>What became of a saving phase.</summary>
/// <param name="Note">One line for the pane: what was written, or why nothing was.</param>
public sealed record PlanSave(bool Saved, string Note);

/// <summary>Writes the file a saving phase names. A seam, so a run can be tested without a model.</summary>
public interface IPlanSaver
{
    /// <param name="reported">What the earlier phases found, as the planner is told it.</param>
    Task<PlanSave> Save(PlanPhase phase, string reported, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes a plan's findings into the folder open on this machine.
///
/// The one step in a planned run that is not a host's. A plan could not say
/// "and save it here" any other way than scp, on a server with no route back to
/// the desk the person is sitting at, so the only thing that happened was a
/// command that failed.
///
/// The model chooses what the file says and nothing else. The path is the
/// phase's, which the person read before pressing Run, and the write goes
/// through the same <see cref="WorkspaceCalls"/> as every other: the policy, the
/// diff at the gate, and the refusal to write a redaction marker into a real
/// file.
/// </summary>
/// <param name="mayWrite">
/// The pane's Write files here switch, asked when the phase is reached rather
/// than when the plan was drafted: it can be switched off in between.
/// </param>
public sealed class PlanSaver(
    IAssistBackend backend,
    IWorkspaceAccess? localWorkspace,
    ICommandGate gate,
    Func<bool> mayWrite) : IPlanSaver
{
    /// <summary>The one tool it is given. No path: the phase has one already.</summary>
    internal const string SaveFile = "save_file";

    /// <summary>
    /// Turns before it gives up: enough to read the file it is replacing and to
    /// be told once that a line of it is not allowed.
    /// </summary>
    private const int Turns = 4;

    /// <summary>A file on this machine was written.</summary>
    public event EventHandler<FileChange>? Wrote;

    /// <summary>A read or a write, as it happens, so the pane can say what it is doing.</summary>
    public event EventHandler<TranscriptEntry.Step>? Looked;

    public async Task<PlanSave> Save(PlanPhase phase, string reported, CancellationToken cancellationToken = default)
    {
        if (phase.Save is not { Length: > 0 } path)
            return new PlanSave(false, "This phase names no file.");

        if (localWorkspace is not { Root.Length: > 0 } here)
            return new PlanSave(false, $"No folder is open on this machine, so {path} was not saved.");

        if (!mayWrite())
            return new PlanSave(false, $"Write files here is switched off, so {path} was not saved.");

        var watched = new Watched(gate);
        var calls = new WorkspaceCalls(
            here,
            watched,
            "this machine",
            entry => Looked?.Invoke(this, (TranscriptEntry.Step)entry),
            entry => Looked?.Invoke(this, (TranscriptEntry.Step)entry));
        calls.Wrote += (_, change) => Wrote?.Invoke(this, change);

        List<AssistMessage> conversation =
        [
            new()
            {
                Role = AssistRole.User,
                Text = string.Join("\n\n",
                    $"Save {path}. What it should contain: {phase.Why ?? phase.Name}",
                    "What the run found:",
                    reported.Length == 0 ? "(nothing was reported)" : reported),
            },
        ];

        IReadOnlyList<AssistTool> offered = [.. WorkspaceTools.ReadingLocal, Tool(path)];
        var budget = new CommandBudget(Turns);
        for (var turn = 0; turn < Turns; turn++)
        {
            var said = new StringBuilder();
            var asked = new List<AssistToolCall>();
            try
            {
                await foreach (var streamed in backend.Stream(
                    new AssistRequest
                    {
                        System = AssistPrompts.PlanSaver(here.RootLabel, path),
                        Messages = [.. conversation],
                        Tools = offered,
                    },
                    cancellationToken))
                {
                    switch (streamed)
                    {
                        case AssistEvent.Say say:
                            said.Append(say.Text);
                            break;
                        case AssistEvent.Call call:
                            asked.Add(call.Tool);
                            break;
                    }
                }
            }
            catch (AssistException e)
            {
                return new PlanSave(false, e.Message);
            }

            if (asked.Count == 0)
            {
                return new PlanSave(false, said.Length > 0
                    ? $"{path} was not saved: {said.ToString().Trim()}"
                    : $"{path} was not saved.");
            }

            conversation.Add(new AssistMessage
            {
                Role = AssistRole.Assistant,
                Text = said.Length == 0 ? null : said.ToString(),
                ToolCalls = asked,
            });

            var results = new List<AssistToolResult>();
            foreach (var call in asked)
            {
                if (call.Name is not (SaveFile or WorkspaceTools.ListLocalFiles or WorkspaceTools.ReadLocalFile))
                {
                    results.Add(new AssistToolResult(
                        call.Id, $"Only {SaveFile} and reading this machine's folder are available here.", Failed: true));
                    continue;
                }

                var (ran, result) = await calls.Carry(
                    call.Name == SaveFile ? AsWrite(call, path) : call,
                    budget,
                    cancellationToken);

                if (call.Name == SaveFile && ran)
                    return new PlanSave(true, result.Output);

                // A person saying no ends it. The model is not asked again,
                // which would only produce the same file with different words.
                if (watched.Refused)
                    return new PlanSave(false, $"You refused the change, so {path} was not saved.");

                results.Add(result);
            }

            conversation.Add(new AssistMessage { Role = AssistRole.User, ToolResults = results });
        }

        return new PlanSave(false, $"{path} was not saved: the assistant did not manage to write it.");
    }

    /// <summary>The model's call, turned into a write to the phase's own path.</summary>
    private static AssistToolCall AsWrite(AssistToolCall call, string path)
    {
        string content = "", why = "";
        try
        {
            using var parsed = JsonDocument.Parse(call.Arguments);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (parsed.RootElement.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString() ?? "";
                if (parsed.RootElement.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String)
                    why = w.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return new AssistToolCall(
            call.Id,
            WorkspaceTools.WriteLocalFile,
            JsonSerializer.Serialize(new { path, content, why }));
    }

    private static AssistTool Tool(string path) => new(
        SaveFile,
        $"Write {path} in the folder open on the user's own machine, replacing it whole if it is there. "
            + "Send the file's complete contents. The user sees exactly what changes and may refuse.",
        """
        {
          "type": "object",
          "properties": {
            "content": {
              "type": "string",
              "description": "The file's complete contents."
            },
            "why": {
              "type": "string",
              "description": "One short sentence, for the user to read, saying what this file is."
            }
          },
          "required": ["content", "why"]
        }
        """);

    /// <summary>The gate, remembering whether a person said no.</summary>
    private sealed class Watched(ICommandGate inner) : ICommandGate
    {
        public bool Refused { get; private set; }

        public async Task<bool> Allow(PendingCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await inner.Allow(command, cancellationToken);
            Refused |= !allowed;
            return allowed;
        }

        public Task<ToolApproval> Allow(PendingToolCall call, CancellationToken cancellationToken = default) =>
            inner.Allow(call, cancellationToken);
    }
}
