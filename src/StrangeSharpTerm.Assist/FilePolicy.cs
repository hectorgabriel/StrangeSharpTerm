using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Assist;

/// <summary>What something wants to do to a file.</summary>
public enum FileOperation
{
    /// <summary>Read a directory.</summary>
    List,

    Read,

    /// <summary>Create it, or replace what is there.</summary>
    Write,

    Delete,
}

/// <summary>
/// What the policy decided about one file operation.
/// </summary>
/// <param name="MayRunUnattended">True only for reading, and only inside the workspace.</param>
/// <param name="Reason">
/// Why a person is being asked, in words fit to put in front of one. Empty when
/// it was allowed.
/// </param>
/// <param name="IsDestructive">
/// It deletes, or it writes to something that decides who can get in. This
/// grants nothing -- it only makes the warning louder -- so being incomplete
/// costs nothing.
/// </param>
/// <param name="IsRefused">
/// Nobody may allow it: it is outside the workspace. The gate is never opened,
/// because there is no question to ask -- the person answered it when they chose
/// the folder.
/// </param>
public sealed record FileJudgement(
    bool MayRunUnattended,
    string Reason = "",
    bool IsDestructive = false,
    bool IsRefused = false)
{
    public static FileJudgement Allowed { get; } = new(true);

    internal static FileJudgement Ask(string reason, bool destructive = false) => new(false, reason, destructive);

    internal static FileJudgement Refuse(string reason) => new(false, reason, IsDestructive: true, IsRefused: true);
}

/// <summary>
/// Whether a file may be touched without a person watching.
///
/// The same shape as <see cref="CommandPolicy"/> and for the same reason, with
/// one difference that matters: a command is judged by what it is, and a file is
/// judged by where it is. The workspace root is the whole of the permission.
/// Choosing <c>~/srv/app</c> is a person saying which files this app may write
/// to, so a path outside it is not a question to put to a gate — there is
/// nothing to weigh, and asking would teach people to say yes.
///
/// Inside the root: reading runs unattended, and every write and delete stops.
/// That asymmetry is the same bet the command policy makes. A read wrongly
/// stopped costs a click; a write wrongly allowed costs a file, and a file is
/// what somebody's afternoon is in.
/// </summary>
public static class FilePolicy
{
    /// <summary>
    /// Judges one operation against a root.
    /// </summary>
    /// <param name="root">The workspace's root, absolute.</param>
    /// <param name="path">The path asked for, absolute or relative to the root.</param>
    public static FileJudgement Judge(FileOperation operation, string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FileJudgement.Refuse("No path was given.");

        var absolute = PosixPath.Normalise(path.StartsWith('/') ? path : PosixPath.Join(root, path));
        if (!PosixPath.IsInside(root, absolute))
            return FileJudgement.Refuse(
                $"{absolute} is outside this workspace, which is {root}. Nothing outside it can be read or written.");

        var shown = PosixPath.Relative(root, absolute);
        return operation switch
        {
            FileOperation.List or FileOperation.Read => FileJudgement.Allowed,

            FileOperation.Write when GuardsAccess(absolute) => FileJudgement.Ask(
                $"It writes {shown}, which decides who can get into this machine.",
                destructive: true),

            FileOperation.Write => FileJudgement.Ask($"It writes {shown}."),

            FileOperation.Delete => FileJudgement.Ask($"It deletes {shown}.", destructive: true),

            _ => FileJudgement.Ask($"It changes {shown}."),
        };
    }

    /// <summary>
    /// Files that decide who can get into the machine, or what it runs on its
    /// own.
    ///
    /// Being inside a workspace does not make these ordinary. A person who
    /// opened their home directory has said the assistant may edit their
    /// project; they have not said it may append a key to
    /// <c>.ssh/authorized_keys</c>, and that write should arrive in red.
    ///
    /// It grants nothing, so it can be incomplete. Every one of these stops at
    /// the gate anyway — the list only decides how loudly.
    /// </summary>
    private static bool GuardsAccess(string path)
    {
        var name = PosixPath.Name(path);
        if (Named.Contains(name))
            return true;

        // A directory rather than a file: everything under .ssh, and everything
        // a shell runs on the way in.
        return path.Contains("/.ssh/", StringComparison.Ordinal)
            || path.Contains("/sudoers.d/", StringComparison.Ordinal)
            || path.Contains("/cron.d/", StringComparison.Ordinal);
    }

    private static readonly HashSet<string> Named = new(StringComparer.Ordinal)
    {
        "authorized_keys",
        "sudoers",
        "shadow",
        "passwd",
        "group",
        "crontab",
        ".bashrc",
        ".bash_profile",
        ".profile",
        ".zshrc",
    };
}
