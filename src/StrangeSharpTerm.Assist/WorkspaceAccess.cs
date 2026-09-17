using System.Text.Json;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Assist;

/// <summary>
/// A write, worked out but not yet done.
/// </summary>
/// <param name="Creates">There is nothing there now. The gate says so, because creating and replacing are not the same act.</param>
/// <param name="Diff">What changes, for the person being asked.</param>
public sealed record FileChange(
    string Path,
    string Relative,
    string Text,
    bool Creates,
    FileDiff Diff,
    string Newline = "\n",
    bool ByteOrderMark = false);

/// <summary>
/// The folder a conversation may work in.
///
/// The seam the agent's file tools are written against, as
/// <see cref="IHostAccess"/> is for commands. It is deliberately narrow: three
/// things a model can ask for, and a fourth — <see cref="Plan"/> — that exists
/// so the gate has something to show. A write is worked out first and carried
/// out afterwards, with a person in between, and those have to be two calls for
/// that person to be anywhere.
///
/// Null where a host has no workspace open, which is the ordinary state. The
/// tools are not offered at all then: a model cannot ask to write to a folder
/// nobody chose.
/// </summary>
public interface IWorkspaceAccess
{
    /// <summary>The root, absolute, as the policy judges paths against it.</summary>
    string Root { get; }

    /// <summary>The root as a person reads it: <c>~/srv/app</c>.</summary>
    string RootLabel { get; }

    /// <summary>One directory, rendered for a model to read.</summary>
    Task<string> List(string path, CancellationToken cancellationToken = default);

    Task<FileText> Read(string path, CancellationToken cancellationToken = default);

    /// <summary>What writing this text would do, without doing it.</summary>
    Task<FileChange> Plan(string path, string text, CancellationToken cancellationToken = default);

    /// <summary>Carries out a change <see cref="Plan"/> worked out and a person allowed.</summary>
    Task Write(FileChange change, CancellationToken cancellationToken = default);
}

/// <summary>
/// The three tools a workspace adds.
///
/// Three rather than one, which is the opposite of the choice
/// <see cref="AssistTools"/> makes for commands — and for a reason that is worth
/// saying. A command is one general thing: a server exposes everything it can do
/// through a shell, so one tool covers it. Files are not that. The gate has to
/// tell reading from writing before it decides whether to stop, and a single
/// <c>file(operation: "write")</c> tool would make that a matter of parsing an
/// argument the model chose. Reading is safe and writing is not, so they are
/// separate tools and the safe one is the one that can be offered alone.
/// </summary>
public static class WorkspaceTools
{
    public const string ListFiles = "list_files";

    public const string ReadFile = "read_file";

    public const string WriteFile = "write_file";

    /// <summary>Whether this name is one of these.</summary>
    public static bool Owns(string name) => name is ListFiles or ReadFile or WriteFile;

    /// <summary>What a model is told it may read. Offered whenever a workspace is open.</summary>
    public static IReadOnlyList<AssistTool> Reading { get; } =
    [
        new(
            ListFiles,
            "List a directory in the open workspace on this host. Paths are relative to the "
                + "workspace root; nothing outside it can be listed.",
            """
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string",
                  "description": "The directory, relative to the workspace root. Use \".\" for the root itself."
                }
              },
              "required": ["path"]
            }
            """),
        new(
            ReadFile,
            "Read a text file in the open workspace on this host. Paths are relative to the "
                + "workspace root; nothing outside it can be read.",
            """
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string",
                  "description": "The file, relative to the workspace root."
                }
              },
              "required": ["path"]
            }
            """),
    ];

    /// <summary>
    /// The one that writes. Offered only where a person has turned editing on
    /// for this conversation, and every call stops at the gate whatever they
    /// turned on.
    /// </summary>
    public static AssistTool Writer { get; } = new(
        WriteFile,
        "Replace a file's contents in the open workspace on this host, creating it if it is not "
            + "there. The whole file is written, so send its whole new contents. The user sees "
            + "exactly what changes and may refuse.",
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "The file, relative to the workspace root."
            },
            "content": {
              "type": "string",
              "description": "The file's complete new contents."
            },
            "why": {
              "type": "string",
              "description": "One short sentence, for the user to read, saying what this change is for."
            }
          },
          "required": ["path", "content", "why"]
        }
        """);

    /// <summary>The path a call names, or empty where it named none this app can read.</summary>
    public static string ReadPath(string? arguments) => Field(arguments, "path");

    /// <summary>A write call's three arguments.</summary>
    public static (string Path, string Content, string Why) ReadWrite(string? arguments) =>
        (Field(arguments, "path"), Field(arguments, "content"), Field(arguments, "why"));

    /// <summary>
    /// One field, as a string whatever the model sent, and empty where the JSON
    /// is not what was asked for. Forgiving in exactly the way
    /// <see cref="AssistTools.ReadRun"/> is: a call this app cannot read comes
    /// back empty and is refused by the policy like anything else it cannot
    /// make sense of, rather than ending the conversation.
    /// </summary>
    private static string Field(string? arguments, string name)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "";

        try
        {
            var root = JsonDocument.Parse(arguments).RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
                return "";
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// A directory as a model reads it: one entry a line, a trailing slash for a
    /// directory, and a size for a file.
    ///
    /// <c>ls -l</c> would do as well and cost a command; this costs the listing
    /// the pane already knows how to ask for, and comes back the same on a host
    /// whose <c>ls</c> is busybox.
    /// </summary>
    public static string Render(string path, IReadOnlyList<RemoteEntry> entries) =>
        entries.Count == 0
            ? $"{path} is empty."
            : string.Join(
                '\n',
                entries.Select(entry => entry.IsDirectory
                    ? $"{entry.Name}/"
                    : $"{entry.Name}\t{entry.Length} bytes"));
}
