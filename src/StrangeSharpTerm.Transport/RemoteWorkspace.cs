using System.Text;

namespace StrangeSharpTerm.Transport;

/// <summary>A path that is not in the workspace. The message is fit to show a person, and to send to a model.</summary>
public sealed class WorkspaceBoundsException(string message) : Exception(message);

/// <summary>
/// A file as an editor and an assistant both need it.
/// </summary>
/// <param name="Newline">
/// What the file's own lines end with, kept so that saving it back does not
/// rewrite every line of a file because one line changed. See
/// <see cref="RemoteWorkspace.Write"/>.
/// </param>
/// <param name="IsBinary">
/// There is a zero byte in it. Nothing here tries to be cleverer than that: it
/// is the test <c>grep</c> and <c>git</c> both use, and what it is for is
/// refusing to put a JPEG in a text editor.
/// </param>
/// <param name="Truncated">The file is longer than the limit; what is here is the start of it.</param>
public sealed record FileText(
    string Path,
    string Relative,
    string Text,
    string Newline,
    bool IsBinary,
    bool Truncated,
    long Length,
    DateTime Modified,
    bool HasByteOrderMark = false);

/// <summary>
/// A directory on a server, and everything inside it, as one thing to open.
///
/// This is the whole of what "workspace" means here: a root, and the rule that
/// nothing outside it is touched. The rule lives here rather than in the pane or
/// in the agent because both of them need it and there must not be two answers —
/// a person who opened <c>~/srv/app</c> has said what the assistant may write
/// to, and a model that asks for <c>/etc/shadow</c> is refused by the same code
/// that greys the button out.
///
/// <para>
/// What it cannot do: see through a symbolic link. A link inside the root
/// pointing at <c>/etc</c> resolves on the server, and this resolves paths as
/// text before anything is sent. Following one would mean a round trip per
/// component on every call, and would still race whoever could replace the link
/// between the check and the write. So the guarantee is honest and narrower than
/// it looks: what is bounded is the path this app asks for, not the inode the
/// server decides that names. The gate is the second half of the answer, and
/// it is the half that has a person in it.
/// </para>
/// </summary>
public sealed class RemoteWorkspace
{
    /// <summary>
    /// How much of a file is read at all.
    ///
    /// An editor pointed at a two gigabyte log by a mistyped path is the case
    /// this is for. Two megabytes is larger than any configuration file anyone
    /// edits and small enough to hold, send and diff without thinking about it.
    /// </summary>
    public const long MaxFileBytes = 2_000_000;

    private readonly IRemoteFiles _files;

    /// <param name="root">
    /// Where the workspace starts, absolute. A relative one is taken against the
    /// account's own directory, which is what typing <c>srv/app</c> into the bar
    /// means.
    /// </param>
    public RemoteWorkspace(IRemoteFiles files, string root)
    {
        _files = files;
        Home = files.Home;
        Root = PosixPath.Normalise(root.StartsWith('/') ? root : PosixPath.Join(files.Home, root)).TrimEnd('/');
        if (Root.Length == 0)
            Root = "/";
    }

    /// <summary>The directory everything is inside, absolute and normalised.</summary>
    public string Root { get; }

    /// <summary>The account's own directory, for showing a root as <c>~/srv/app</c>.</summary>
    public string Home { get; }

    /// <summary>The root as a person reads it.</summary>
    public string RootLabel => PosixPath.Display(Home, Root);

    /// <summary>
    /// One path inside this workspace, absolute.
    ///
    /// Everything else here goes through it, so there is one place that decides
    /// what is in and what is out. A relative path is taken against the root; an
    /// absolute one is checked against it and refused if it is elsewhere.
    /// </summary>
    /// <exception cref="WorkspaceBoundsException">The path is outside the root.</exception>
    public string Resolve(string path)
    {
        var absolute = PosixPath.Normalise(path.StartsWith('/') ? path : PosixPath.Join(Root, path));
        if (!PosixPath.IsInside(Root, absolute))
            throw new WorkspaceBoundsException(
                $"{absolute} is outside this workspace, which is {RootLabel}.");
        return absolute;
    }

    /// <summary>Whether a path is one this workspace would touch at all.</summary>
    public bool Contains(string path)
    {
        try
        {
            Resolve(path);
            return true;
        }
        catch (WorkspaceBoundsException)
        {
            return false;
        }
    }

    /// <summary>How a path reads in this workspace: <c>conf/nginx.conf</c>.</summary>
    public string Relative(string path) => PosixPath.Relative(Root, path);

    /// <summary>One directory, ordered as a file tree shows it: directories first, then by name.</summary>
    public IReadOnlyList<RemoteEntry> List(string path = ".") =>
    [
        .. _files.List(Resolve(path))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
    ];

    public RemoteEntry? Stat(string path) => _files.Stat(Resolve(path));

    /// <summary>
    /// A file's text, or the fact that it is not text.
    ///
    /// The bytes are read once and everything else is decided from them: whether
    /// there is a zero byte in it, what its lines end with, whether it had a byte
    /// order mark. Asking the server any of those separately would be another
    /// round trip to learn something already in hand.
    /// </summary>
    public FileText Read(string path)
    {
        var absolute = Resolve(path);
        var stat = _files.Stat(absolute)
            ?? throw new FileNotFoundException($"There is no {Relative(absolute)} in this workspace.", absolute);
        var bytes = _files.Read(absolute, MaxFileBytes);

        var binary = bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0;
        var mark = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = binary ? "" : new UTF8Encoding(false).GetString(mark ? bytes.AsSpan(3) : bytes);

        return new FileText(
            absolute,
            Relative(absolute),
            // One kind of line ending in the editor, whatever the file has. What
            // it had is remembered and put back on the way out.
            text.Replace("\r\n", "\n"),
            NewlineOf(text),
            binary,
            // Compared against what the server says the file is, not against the
            // limit: a file of exactly the limit is not truncated, and one longer
            // than it read short whatever came back.
            stat.Length > bytes.Length,
            stat.Length,
            stat.Modified,
            mark);
    }

    /// <summary>
    /// Writes a file back, in the line endings it already had.
    ///
    /// This is the CRLF rule the rest of the app follows, in the one place a
    /// person can most easily break it: the text box on a Windows machine ends
    /// every line with a carriage return, so saving a Linux host's
    /// <c>nginx.conf</c> from Windows without this would rewrite all four hundred
    /// lines of it to change one — and <c>git diff</c> on the server would show
    /// exactly that.
    /// </summary>
    /// <param name="newline">
    /// What the file had, from <see cref="Read"/>. A new file gets <c>\n</c>,
    /// because a file created on a Unix host is a Unix file.
    /// </param>
    public void Write(string path, string text, string newline = "\n", bool byteOrderMark = false)
    {
        var absolute = Resolve(path);
        var normalised = text.Replace("\r\n", "\n");
        var content = newline == "\n" ? normalised : normalised.Replace("\n", newline);

        var encoded = new UTF8Encoding(false).GetBytes(content);
        _files.Write(
            absolute,
            byteOrderMark ? [0xEF, 0xBB, 0xBF, .. encoded] : encoded);
    }

    /// <summary>Copies a local file in. The name is the caller's: dropping a file keeps its own.</summary>
    public void Upload(string localPath, string path) => _files.Upload(localPath, Resolve(path));

    public void Download(string path, string localPath) => _files.Download(Resolve(path), localPath);

    public void CreateDirectory(string path) => _files.CreateDirectory(Resolve(path));

    /// <summary>Creates an empty file, refusing to flatten one that is already there.</summary>
    public void CreateFile(string path)
    {
        var absolute = Resolve(path);
        if (_files.Stat(absolute) is not null)
            throw new IOException($"{Relative(absolute)} already exists.");
        _files.Write(absolute, []);
    }

    public void Delete(RemoteEntry entry)
    {
        Resolve(entry.Path);
        _files.Delete(entry);
    }

    /// <summary>Renames inside the workspace. Both ends are checked, so this is not a way out of it.</summary>
    public void Rename(string path, string newPath) => _files.Rename(Resolve(path), Resolve(newPath));

    /// <summary>
    /// What a file's lines end with, by counting rather than by looking at the
    /// first one: a file that is mostly CRLF with one stray LF is a CRLF file,
    /// and saving it as LF would rewrite the lot.
    /// </summary>
    private static string NewlineOf(string text)
    {
        var carriage = 0;
        var feeds = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
                continue;
            feeds++;
            if (index > 0 && text[index - 1] == '\r')
                carriage++;
        }

        return feeds > 0 && carriage * 2 > feeds ? "\r\n" : "\n";
    }
}
