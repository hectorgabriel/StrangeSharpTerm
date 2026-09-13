namespace StrangeSharpTerm.Mcp;

/// <summary>
/// Finds a bare command.
///
/// An app launched from Finder inherits launchd's <c>PATH</c> rather than your
/// shell's, so <c>npx</c> and <c>uvx</c> are otherwise invisible — which is the
/// single most common way a local server fails to start, and it fails looking
/// like the server's fault.
///
/// Resolving it by running a login shell was the alternative and is worse:
/// executing someone's dotfiles to start a tool server is a far larger thing to
/// do than looking in four directories.
/// </summary>
public static class CommandPath
{
    /// <summary>
    /// Where to look, after <c>PATH</c> itself. Homebrew on both architectures,
    /// the traditional local prefix, and the per-user one the Python and Node
    /// tools install into.
    /// </summary>
    public static IReadOnlyList<string> ExtraDirectories { get; } = OperatingSystem.IsWindows()
        ?
        [
            Path.Combine(Home, "AppData", "Roaming", "npm"),
            Path.Combine(Home, ".local", "bin"),
        ]
        :
        [
            "/opt/homebrew/bin",
            "/usr/local/bin",
            Path.Combine(Home, ".local", "bin"),
            Path.Combine(Home, ".cargo", "bin"),
            "/usr/bin",
            "/bin",
        ];

    /// <summary>
    /// The command as it should be executed.
    ///
    /// A path with a separator in it is taken as given — someone who wrote one
    /// has said where the thing is. A bare name is looked for on <c>PATH</c>
    /// and then in <see cref="ExtraDirectories"/>, and comes back unchanged when
    /// it is nowhere: launching it anyway produces the operating system's own
    /// error, which says more than anything invented here would.
    /// </summary>
    public static string Resolve(string command, IEnumerable<string>? extra = null)
    {
        if (command.Length == 0 || command.Contains('/') || command.Contains('\\'))
            return command;

        foreach (var directory in Directories(extra))
        {
            if (Executable(directory, command) is { } found)
                return found;
        }
        return command;
    }

    /// <summary>Whether a bare command can be found at all, for a settings row to say so before it is tried.</summary>
    public static bool Exists(string command, IEnumerable<string>? extra = null) =>
        command.Length > 0
        && (command.Contains('/') || command.Contains('\\')
            ? File.Exists(command)
            : Resolve(command, extra) != command);

    private static IEnumerable<string> Directories(IEnumerable<string>? extra)
    {
        // PATH first: an explicitly configured environment beats a guess about
        // where things usually live.
        foreach (var directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return directory;
        }

        foreach (var directory in extra ?? ExtraDirectories)
            yield return directory;
    }

    /// <summary>
    /// The file, if it is there and can be run.
    ///
    /// Windows names an executable by extension and Unix by a permission bit,
    /// so the question is a different one on each.
    /// </summary>
    private static string? Executable(string directory, string command)
    {
        if (directory.Length == 0)
            return null;

        foreach (var name in Candidates(command))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, name);
            }
            catch (ArgumentException)
            {
                // A PATH entry with an illegal character in it. Someone else's
                // problem, and not a reason to stop looking.
                return null;
            }

            if (!File.Exists(candidate))
                continue;
            if (OperatingSystem.IsWindows() || IsExecutable(candidate))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string command)
    {
        yield return command;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(command))
            yield break;

        // PATHEXT is the list Windows itself uses, and npx arrives as npx.cmd.
        foreach (var extension in (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return command + extension.ToLowerInvariant();
        }
    }

    private static bool IsExecutable(string path)
    {
        // Guarded rather than suppressed: the caller already knows the platform,
        // but the analyser reads this method on its own and is right to.
        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            return File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);
        }
        catch (Exception)
        {
            // Unreadable, or a filesystem with no such notion. Treat it as
            // runnable and let the launch say otherwise.
            return true;
        }
    }

    private static string Home =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
}
