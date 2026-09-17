namespace StrangeSharpTerm.Transport;

/// <summary>
/// Paths on the far end, which are POSIX paths whatever this machine is.
///
/// <see cref="System.IO.Path"/> is the wrong tool for every one of these:
/// running on Windows it joins with a backslash, treats <c>C:</c> as a root and
/// calls <c>/etc/nginx</c> rooted at whatever drive the app happens to be on.
/// The server is a Unix host in all three cases, so the rules are the server's.
/// </summary>
public static class PosixPath
{
    /// <summary>A path joined the remote way: one separator, whatever the pieces end with.</summary>
    public static string Join(string directory, string name)
    {
        if (name.StartsWith('/'))
            return name;
        if (directory.Length == 0)
            return name;
        return directory.EndsWith('/') ? directory + name : $"{directory}/{name}";
    }

    /// <summary>The directory above, or null at the root.</summary>
    public static string? Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
            return null;
        var cut = trimmed.LastIndexOf('/');
        return cut switch
        {
            < 0 => null,
            0 => "/",
            _ => trimmed[..cut],
        };
    }

    /// <summary>The last component: what a file is called, without the directories above it.</summary>
    public static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        var cut = trimmed.LastIndexOf('/');
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>
    /// A path with <c>.</c>, <c>..</c> and empty components taken out.
    ///
    /// Textual, and deliberately so: this is what decides whether a path is
    /// inside a workspace, and that decision has to be made before anything is
    /// sent to a server rather than after. It cannot see through a symbolic
    /// link — see <see cref="RemoteWorkspace"/>, which says what that costs.
    /// </summary>
    public static string Normalise(string path)
    {
        var rooted = path.StartsWith('/');
        List<string> parts = [];
        foreach (var part in path.Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    break;

                case "..":
                    // Above the root is still the root, as every shell has it:
                    // /.. is /, and a relative ..\ that climbs past the start
                    // keeps the climb so the caller can see it left.
                    if (parts.Count > 0 && parts[^1] != "..")
                        parts.RemoveAt(parts.Count - 1);
                    else if (!rooted)
                        parts.Add("..");
                    break;

                default:
                    parts.Add(part);
                    break;
            }
        }

        var joined = string.Join('/', parts);
        return rooted ? "/" + joined : joined;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is the directory <paramref name="root"/>
    /// or something under it. Both are normalised first.
    /// </summary>
    public static bool IsInside(string root, string path)
    {
        var top = Normalise(root).TrimEnd('/');
        var candidate = Normalise(path).TrimEnd('/');
        if (top.Length == 0)
            top = "/";
        if (candidate.Length == 0)
            candidate = "/";

        if (candidate == top)
            return true;
        // The separator matters: /srv/app-backup is not inside /srv/app, and a
        // prefix test without it says it is.
        return candidate.StartsWith(top == "/" ? "/" : top + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// How a path reads against a root: <c>conf/nginx.conf</c> rather than the
    /// whole thing. The root itself is <c>.</c>, as a shell shows it.
    /// </summary>
    public static string Relative(string root, string path)
    {
        var top = Normalise(root).TrimEnd('/');
        var candidate = Normalise(path);
        if (top.Length == 0)
            top = "/";
        if (candidate == top || candidate.TrimEnd('/') == top)
            return ".";
        var prefix = top == "/" ? "/" : top + "/";
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ? candidate[prefix.Length..] : candidate;
    }

    /// <summary>
    /// A path with the account's own directory put back as <c>~</c>, for
    /// showing rather than for sending.
    /// </summary>
    public static string Display(string home, string path) =>
        home.Length > 0 && IsInside(home, path)
            ? Relative(home, path) is "." ? "~" : "~/" + Relative(home, path)
            : path;
}
