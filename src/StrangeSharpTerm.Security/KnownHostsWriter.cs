namespace StrangeSharpTerm.Security;

/// <summary>
/// Appends accepted keys to a <c>known_hosts</c> file.
///
/// Trust is recorded in OpenSSH's own file rather than somewhere private, so a
/// host trusted here is equally trusted by <c>ssh</c> on the command line, and by
/// every other tool the user already has. That property is deliberate: it is the
/// reason this app is worth using alongside the rest of a shell workflow.
/// </summary>
public sealed class KnownHostsWriter(string path)
{
    public string Path { get; } = path;

    /// <summary>
    /// <c>~/.ssh/known_hosts</c>, which is what ssh uses when nothing overrides
    /// it, and <c>%USERPROFILE%\.ssh\known_hosts</c> on Windows, which is what
    /// Win32-OpenSSH uses.
    /// </summary>
    public static KnownHostsWriter SystemDefault() => new(DefaultPath());

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    /// <summary>Records a host key as trusted.</summary>
    public void Append(string host, int? port, string keyType, string keyBase64)
    {
        var line = $"{KnownHostsFile.HostPattern(host, port)} {keyType} {keyBase64}\n";
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            // ssh refuses to use ~/.ssh if others can write to it. Windows has no
            // equivalent bit and inherits usable ACLs from the profile directory.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var existed = File.Exists(Path);
        // Appended rather than rewritten: this file is shared with ssh itself, and
        // rewriting it would risk losing entries added by anything else between
        // our read and our write.
        File.AppendAllText(Path, line);

        if (!existed && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
