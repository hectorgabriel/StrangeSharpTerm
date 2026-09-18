namespace StrangeSharpTerm.Transport;

/// <summary>
/// This machine's own files, behind the seam the far end's files arrive
/// through.
///
/// The point of implementing <see cref="IRemoteFiles"/> rather than something
/// new is that everything above it — the tree, the editor, the workspace's
/// root rule, the policy that judges a write, the tools the assistant is
/// offered — was written against a directory, not against SFTP. A local
/// filesystem is a directory. Nothing above this had to learn a second way of
/// listing, reading or saving, and the two workspaces cannot drift apart.
///
/// <para>
/// Paths are POSIX here, even on Windows. A drive is addressed as
/// <c>/C:/Users/you</c>, leading slash and all — which is not an invention:
/// Windows OpenSSH's own sftp-server says it exactly that way, and the
/// integration test has been speaking it since M2. The alternative was teaching
/// <see cref="PosixPath"/>, <see cref="RemoteWorkspace"/> and everything holding
/// them about drive letters and backslashes, so that the one thing that decides
/// whether a path is inside a workspace would have two dialects. One dialect,
/// translated at the edge, is the cheaper promise to keep.
/// </para>
/// </summary>
public sealed class LocalFiles : IRemoteFiles
{
    /// <param name="home">
    /// Where a browser starts. The account's own directory unless a test says
    /// otherwise — which is how these are exercised without touching anybody's
    /// documents.
    /// </param>
    public LocalFiles(string? home = null) =>
        Home = Posix(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The account's own directory, in the POSIX spelling everything above uses.</summary>
    public string Home { get; }

    /// <summary>Whether this machine spells its paths with drive letters and backslashes.</summary>
    private static bool Windows => OperatingSystem.IsWindows();

    /// <summary>
    /// A path as everything above this says it: forward slashes, and a leading
    /// slash even in front of a drive.
    /// </summary>
    public static string Posix(string path)
    {
        if (!Windows)
            return path;

        var forward = path.Replace('\\', '/');
        return forward.StartsWith('/') ? forward : "/" + forward;
    }

    /// <summary>
    /// The same path as this machine's own API wants it, which on Windows means
    /// taking the leading slash back off and putting the backslashes back.
    /// </summary>
    public static string Native(string path)
    {
        if (!Windows)
            return path;

        var trimmed = path.StartsWith('/') ? path[1..] : path;
        return trimmed.Replace('/', '\\');
    }

    public IReadOnlyList<RemoteEntry> List(string path)
    {
        var native = Native(path);
        var found = new List<RemoteEntry>();

        foreach (var entry in new DirectoryInfo(native).EnumerateFileSystemInfos())
        {
            // A file that goes away between the listing and the question about
            // it is ordinary on a machine somebody is using, and not worth
            // failing a whole directory over.
            try
            {
                found.Add(Describe(entry));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return found;
    }

    public RemoteEntry? Stat(string path)
    {
        var native = Native(path);
        if (Directory.Exists(native))
            return Describe(new DirectoryInfo(native));
        return File.Exists(native) ? Describe(new FileInfo(native)) : null;
    }

    public byte[] Read(string path, long limit)
    {
        using var file = File.OpenRead(Native(path));
        var buffer = new MemoryStream();
        var block = new byte[81920];
        while (buffer.Length < limit)
        {
            var read = file.Read(block, 0, (int)Math.Min(block.Length, limit - buffer.Length));
            if (read == 0)
                break;
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Replaces a file's contents, creating it where there is none.
    ///
    /// Written through the existing file rather than to a temporary one that is
    /// renamed over it, for the same reason the SFTP side does: a new file
    /// carries fresh permissions, and saving somebody's <c>~/.ssh/config</c>
    /// should not quietly change who can read it.
    /// </summary>
    public void Write(string path, byte[] content) => File.WriteAllBytes(Native(path), content);

    public void Download(string remotePath, string localPath) => File.Copy(Native(remotePath), localPath, overwrite: true);

    public void Upload(string localPath, string remotePath) => File.Copy(localPath, Native(remotePath), overwrite: true);

    public void Delete(RemoteEntry entry)
    {
        var native = Native(entry.Path);
        if (entry.IsDirectory)
            Directory.Delete(native, recursive: true);
        else
            File.Delete(native);
    }

    public void Rename(string path, string newPath)
    {
        var from = Native(path);
        var to = Native(newPath);
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to, overwrite: false);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(Native(path));

    /// <summary>
    /// One entry, said the way a listing says it.
    ///
    /// A symbolic link is reported as one and otherwise described by what it
    /// points at, which is what the SFTP side does — the far end resolves it on
    /// the way in, and here <see cref="FileSystemInfo"/> does.
    /// </summary>
    private static RemoteEntry Describe(FileSystemInfo entry)
    {
        var directory = entry is DirectoryInfo || (entry.Attributes & FileAttributes.Directory) != 0;
        return new RemoteEntry(
            entry.Name,
            Posix(entry.FullName),
            directory,
            entry.LinkTarget is not null,
            entry is FileInfo file && !directory ? file.Length : 0,
            entry.LastWriteTime);
    }
}
