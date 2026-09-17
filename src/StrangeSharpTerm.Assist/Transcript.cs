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
/// A command the assistant is running on a host, and then the same one once it
/// has finished.
///
/// Raised by both agents -- the one conversation about a single host and the one
/// driving a fleet -- because a window showing that host says the same thing
/// either way. Narrating a command got one implementation, not two.
/// </summary>
/// <param name="Running">
/// True on the way in and false on the way out, so a pane can show that the
/// assistant has this host and then let go of it again.
/// </param>
public sealed record AssistStep(
    string Host,
    string Command,
    string Why,
    bool Running,
    int? ExitStatus = null,
    string Output = "");

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
    /// The same tool, for an assistant looking at several hosts at once.
    ///
    /// The host is an argument here, which is exactly what the per-host agent
    /// avoids: there, one conversation is about one machine and the host is
    /// implied by which agent is asking. A fleet has one conversation about all
    /// of them, so every call has to say which one it means.
    /// </summary>
    public static AssistTool FleetRunner(IReadOnlyList<string> hosts) => new(
        RunCommand,
        "Run a shell command on one of the hosts and return its output. Read-only commands "
            + "run immediately; anything else stops and asks the user, who may refuse.",
        $$"""
        {
          "type": "object",
          "properties": {
            "host": {
              "type": "string",
              "description": "Which host to run it on, by the name the user gave it.",
              "enum": [{{string.Join(", ", hosts.Select(host => JsonSerializer.Serialize(host)))}}]
            },
            "command": {
              "type": "string",
              "description": "The command to run, exactly as it would be typed."
            },
            "why": {
              "type": "string",
              "description": "One short sentence, for the user to read, saying what this is for."
            }
          },
          "required": ["host", "command", "why"]
        }
        """);

    /// <summary>The host a fleet call names, or empty when it named none this run knows.</summary>
    public static string ReadHost(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "";

        try
        {
            return Field(JsonDocument.Parse(arguments).RootElement, "host");
        }
        catch (JsonException)
        {
            return "";
        }
    }

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
    }

    /// <summary>
    /// One field of a call's arguments, as a string whatever the model sent.
    ///
    /// Shared by both readers, and forgiving in the same way: a number where a
    /// string belongs is read rather than refused, and a field that is not
    /// there comes back empty for the gate to turn down like anything else it
    /// cannot read.
    /// </summary>
    private static string Field(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
            : "";
}
