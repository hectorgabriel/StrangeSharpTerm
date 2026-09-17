using System.Text;

namespace StrangeSharpTerm.Transport.Tests;

/// <summary>
/// A filesystem in memory, so the rules a workspace enforces can be checked
/// without a server.
///
/// Everything interesting about <see cref="RemoteWorkspace"/> is a decision —
/// what counts as inside, what a text file is, what a save does to line endings
/// — and not one of those decisions needs SFTP to make.
/// </summary>
internal sealed class MemoryFiles : IRemoteFiles
{
    private readonly Dictionary<string, byte[]> _files = [];
    private readonly HashSet<string> _directories = ["/", "/home", "/home/ops"];

    public string Home { get; set; } = "/home/ops";

    /// <summary>Every path that was written, in order, so a test can see what reached the server.</summary>
    public List<string> Wrote { get; } = [];

    public MemoryFiles With(string path, string content)
    {
        _files[path] = Encoding.UTF8.GetBytes(content);
        for (var parent = PosixPath.Parent(path); parent is not null; parent = PosixPath.Parent(parent))
            _directories.Add(parent);
        return this;
    }

    public MemoryFiles WithBytes(string path, params byte[] content)
    {
        _files[path] = content;
        return this;
    }

    public string Text(string path) => Encoding.UTF8.GetString(_files[path]);

    public IReadOnlyList<RemoteEntry> List(string path) =>
    [
        .. _directories.Where(directory => PosixPath.Parent(directory) == path)
            .Select(directory => new RemoteEntry(
                PosixPath.Name(directory), directory, true, false, 4096, new DateTime(2026, 9, 1))),
        .. _files.Where(file => PosixPath.Parent(file.Key) == path)
            .Select(file => new RemoteEntry(
                PosixPath.Name(file.Key), file.Key, false, false, file.Value.Length, new DateTime(2026, 9, 2))),
    ];

    public RemoteEntry? Stat(string path)
    {
        if (_directories.Contains(path))
            return new RemoteEntry(PosixPath.Name(path), path, true, false, 4096, new DateTime(2026, 9, 1));
        return _files.TryGetValue(path, out var content)
            ? new RemoteEntry(PosixPath.Name(path), path, false, false, content.Length, new DateTime(2026, 9, 2))
            : null;
    }

    public byte[] Read(string path, long limit) =>
        _files.TryGetValue(path, out var content)
            ? content[..(int)Math.Min(content.Length, limit)]
            : throw new FileNotFoundException(path);

    public void Write(string path, byte[] content)
    {
        Wrote.Add(path);
        _files[path] = content;
    }

    public void Download(string remotePath, string localPath) =>
        File.WriteAllBytes(localPath, _files[remotePath]);

    public void Upload(string localPath, string remotePath) =>
        Write(remotePath, File.ReadAllBytes(localPath));

    public void Delete(RemoteEntry entry) => _files.Remove(entry.Path);

    public void Rename(string path, string newPath)
    {
        _files[newPath] = _files[path];
        _files.Remove(path);
    }

    public void CreateDirectory(string path) => _directories.Add(path);
}

public class PosixPathTests
{
    [Theory]
    [InlineData("/srv/app/./conf", "/srv/app/conf")]
    [InlineData("/srv/app/conf/..", "/srv/app")]
    [InlineData("/srv//app///conf", "/srv/app/conf")]
    [InlineData("/../etc", "/etc")]
    [InlineData("conf/../../etc", "../etc")]
    public void ItResolvesDotsBeforeAnythingIsSent(string path, string expected) =>
        PosixPath.Normalise(path).ShouldBe(expected);

    [Fact]
    public void ANeighbourWithTheSamePrefixIsNotInside()
    {
        // The separator is the whole of this test: /srv/app-backup starts with
        // /srv/app, and a prefix check without it would hand a workspace the
        // directory next door.
        PosixPath.IsInside("/srv/app", "/srv/app-backup").ShouldBeFalse();
        PosixPath.IsInside("/srv/app", "/srv/app/conf").ShouldBeTrue();
        PosixPath.IsInside("/srv/app", "/srv/app").ShouldBeTrue();
    }

    [Fact]
    public void AHomeDirectoryIsShownAsATilde()
    {
        PosixPath.Display("/home/ops", "/home/ops/srv/app").ShouldBe("~/srv/app");
        PosixPath.Display("/home/ops", "/home/ops").ShouldBe("~");
        PosixPath.Display("/home/ops", "/etc/nginx").ShouldBe("/etc/nginx");
    }
}

public class RemoteWorkspaceTests
{
    private static RemoteWorkspace Workspace(MemoryFiles files, string root = "/home/ops/srv/app") =>
        new(files, root);

    [Fact]
    public void ARelativeRootIsTakenAgainstTheAccountsOwnDirectory() =>
        Workspace(new MemoryFiles(), "srv/app").Root.ShouldBe("/home/ops/srv/app");

    [Fact]
    public void ClimbingOutOfTheRootIsRefusedBeforeAnythingIsSent()
    {
        var files = new MemoryFiles().With("/etc/shadow", "root:x:");
        var workspace = Workspace(files);

        Should.Throw<WorkspaceBoundsException>(() => workspace.Resolve("../../../etc/shadow"));
        Should.Throw<WorkspaceBoundsException>(() => workspace.Resolve("/etc/shadow"));
        workspace.Contains("conf/nginx.conf").ShouldBeTrue();
    }

    [Fact]
    public void AFileWithAZeroByteInItIsNotOfferedAsText()
    {
        var files = new MemoryFiles().WithBytes("/home/ops/srv/app/logo.png", 0x89, 0x50, 0x00, 0x1A);

        var read = Workspace(files).Read("logo.png");

        read.IsBinary.ShouldBeTrue();
        read.Text.ShouldBeEmpty();
    }

    [Fact]
    public void SavingKeepsTheLineEndingsTheFileAlreadyHad()
    {
        // The case this exists for: a Windows machine editing a Linux host's
        // configuration. The text box hands back CRLF, and writing that would
        // rewrite every line of the file to change one.
        var files = new MemoryFiles().With("/home/ops/srv/app/unix.conf", "listen 80;\nserver_name x;\n");
        var workspace = Workspace(files);
        var read = workspace.Read("unix.conf");

        read.Newline.ShouldBe("\n");
        workspace.Write("unix.conf", "listen 8080;\r\nserver_name x;\r\n", read.Newline);

        files.Text("/home/ops/srv/app/unix.conf").ShouldBe("listen 8080;\nserver_name x;\n");
    }

    [Fact]
    public void AFileThatWasAlreadyCarriageReturnedStaysThatWay()
    {
        var files = new MemoryFiles().With("/home/ops/srv/app/dos.bat", "echo one\r\necho two\r\n");
        var workspace = Workspace(files);
        var read = workspace.Read("dos.bat");

        read.Newline.ShouldBe("\r\n");
        // What the editor holds is always plain: the file's own endings go back
        // on at the last moment.
        read.Text.ShouldBe("echo one\necho two\n");

        workspace.Write("dos.bat", read.Text.Replace("two", "three"), read.Newline);
        files.Text("/home/ops/srv/app/dos.bat").ShouldBe("echo one\r\necho three\r\n");
    }

    [Fact]
    public void AFileLongerThanTheLimitComesBackSayingSo()
    {
        var files = new MemoryFiles().With(
            "/home/ops/srv/app/huge.log",
            new string('x', (int)RemoteWorkspace.MaxFileBytes + 500));

        var read = Workspace(files).Read("huge.log");

        read.Truncated.ShouldBeTrue();
        read.Text.Length.ShouldBe((int)RemoteWorkspace.MaxFileBytes);
    }

    [Fact]
    public void ListingPutsDirectoriesFirst()
    {
        var files = new MemoryFiles()
            .With("/home/ops/srv/app/zebra.txt", "z")
            .With("/home/ops/srv/app/conf/nginx.conf", "server {}")
            .With("/home/ops/srv/app/app.py", "print()");

        Workspace(files).List(".").Select(entry => entry.Name)
            .ShouldBe(["conf", "app.py", "zebra.txt"]);
    }

    [Fact]
    public void RenamingCannotBeAWayOutOfTheWorkspace()
    {
        var files = new MemoryFiles().With("/home/ops/srv/app/app.py", "print()");

        Should.Throw<WorkspaceBoundsException>(
            () => Workspace(files).Rename("app.py", "../../../tmp/app.py"));
    }

    [Fact]
    public void CreatingAFileWillNotFlattenOneThatIsThere()
    {
        var files = new MemoryFiles().With("/home/ops/srv/app/app.py", "print()");

        Should.Throw<IOException>(() => Workspace(files).CreateFile("app.py"));
        files.Text("/home/ops/srv/app/app.py").ShouldBe("print()");
    }
}
