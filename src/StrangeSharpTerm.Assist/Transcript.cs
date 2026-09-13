using System.Text.Json;

namespace StrangeSharpTerm.Assist;

/// <summary>Where a command got to.</summary>
public enum StepState
{
    /// <summary>The gate is open and a person has not answered yet.</summary>
    Waiting,

    Running,

    /// <summary>It ran and returned, whatever its exit status.</summary>
    Ran,

    /// <summary>A person said no.</summary>
    Refused,

    /// <summary>The pane stopped waiting. The command was not killed; nothing can kill it.</summary>
    TimedOut,

    /// <summary>It could not be run at all: the host went away, or the channel failed.</summary>
    Failed,

    /// <summary>The budget was spent before this one got a turn.</summary>
    Skipped,
}

/// <summary>
/// One row in a conversation, as the pane draws it.
///
/// A command is a row in the same list as the answer, deliberately: what the
/// assistant actually ran should never require looking somewhere else.
/// </summary>
public abstract record TranscriptEntry
{
    private TranscriptEntry() { }

    public sealed record Question(string Text) : TranscriptEntry;

    /// <summary>What the model said, growing as it says it.</summary>
    public sealed record Answer : TranscriptEntry
    {
        public string Markdown { get; set; } = "";

        /// <summary>Summarised reasoning, where the provider offers it. Shown while it is thinking.</summary>
        public string Reasoning { get; set; } = "";

        /// <summary>Prose and fenced blocks, for a pane that puts a button beside the shell ones.</summary>
        public IReadOnlyList<AnswerBlock> Blocks => Fences.Parse(Markdown);
    }

    /// <summary>A command: what it was, why it wanted it, and what came back.</summary>
    public sealed record Step : TranscriptEntry
    {
        public required string Host { get; init; }

        public required string Command { get; init; }

        /// <summary>The model's own reason, shown under the command.</summary>
        public required string Why { get; init; }

        public StepState State { get; set; } = StepState.Waiting;

        /// <summary>Why a person was asked. Empty when the policy let it through.</summary>
        public string Gate { get; init; } = "";

        public bool IsDestructive { get; init; }

        /// <summary>Where the call goes, for a connected tool. Null for a command, which goes to this host.</summary>
        public string? Destination { get; init; }

        /// <summary>The server's own claim that its tool only reads. Shown, never acted on.</summary>
        public bool ReadOnlyClaim { get; init; }

        /// <summary>Whether this row is a connected tool rather than a command on the host.</summary>
        public bool IsTool => Destination is not null;

        public string Output { get; set; } = "";

        public int? ExitStatus { get; set; }

        /// <summary>Whether it ran without anybody being asked, which the row labels "auto".</summary>
        public bool RanUnattended => Gate.Length == 0;
    }

    /// <summary>Something the app has to say: a budget spent, a refusal, a provider that would not answer.</summary>
    public sealed record Note(string Text) : TranscriptEntry;
}

/// <summary>
/// The one tool.
///
/// One rather than several because every extra tool is another thing the gate
/// has to understand, and because a command is already the general case: a
/// server exposes everything it can do through one.
/// </summary>
public static class AssistTools
{
    public const string RunCommand = "run_command";

    public static AssistTool Runner { get; } = new(
        RunCommand,
        "Run a shell command on this host and return its output. Read-only commands run "
            + "immediately; anything else stops and asks the user, who may refuse.",
        """
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "The command to run, exactly as it would be typed."
            },
            "why": {
              "type": "string",
              "description": "One short sentence, for the user to read, saying what this is for."
            }
          },
          "required": ["command", "why"]
        }
        """);

    /// <summary>
    /// Reads a call's arguments.
    ///
    /// A model sometimes sends a number, a missing field, or JSON that is nearly
    /// right; none of that is worth ending a conversation over, so the command
    /// comes back empty and the gate refuses it like anything else it cannot
    /// read.
    /// </summary>
    public static (string Command, string Why) ReadRun(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return ("", "");

        try
        {
            var root = JsonDocument.Parse(arguments).RootElement;
            return (Field(root, "command"), Field(root, "why"));
        }
        catch (JsonException)
        {
            return ("", "");
        }

        static string Field(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
                : "";
    }
}
