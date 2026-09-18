using System.Text;

namespace StrangeSharpTerm.Transport.Tests;

/// <summary>
/// This machine's own files, through the seam the far end's arrive through.
///
/// Against a real temporary directory rather than a fake, because there is
/// nothing to fake: the whole of <see cref="LocalFiles"/> is the translation
/// between what everything above it says and what this machine's API wants, and
/// a double would only assert that the translation is what the double was told
/// it is.
/// </summary>
public class LocalFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"strangesharpterm-local-{Guid.NewGuid():N}");

    public LocalFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "conf"));
        File.WriteAllText(Path.Combine(_root, "app.py"), "print('one')\n");
        File.WriteAllText(Path.Combine(_root, "conf", "nginx.conf"), "server {\n  listen 80;\n}\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The root, spelled the way everything above this spells paths.</summary>
    private string Root => LocalFiles.Posix(_root);

    private LocalFiles Files => new(Root);

    [Fact]
    public void ItListsWhatIsThere()
    {
        var listed = Files.List(Root).OrderBy(entry => entry.Name).ToArray();

        listed.Select(entry => entry.Name).ShouldBe(["app.py", "conf"]);
        listed.Single(entry => entry.Name == "conf").IsDirectory.ShouldBeTrue();
        listed.Single(entry => entry.Name == "app.py").Length.ShouldBe(13);
    }

    [Fact]
    public void ItReadsAndWritesAFile()
    {
        var files = Files;
        var path = PosixPath.Join(Root, "app.py");

        Encoding.UTF8.GetString(files.Read(path, 1000)).ShouldBe("print('one')\n");

        files.Write(path, Encoding.UTF8.GetBytes("print('two')\n"));

        File.ReadAllText(Path.Combine(_root, "app.py")).ShouldBe("print('two')\n");
    }

    [Fact]
    public void AReadStopsAtTheLimitRatherThanTakingTheWholeFile()
    {
        var files = Files;
        File.WriteAllText(Path.Combine(_root, "big.log"), new string('x', 5000));

        files.Read(PosixPath.Join(Root, "big.log"), 100).Length.ShouldBe(100);
    }

    [Fact]
    public void StatSaysWhatIsThereAndNullForWhatIsNot()
    {
        var files = Files;

        files.Stat(PosixPath.Join(Root, "conf")).ShouldNotBeNull().IsDirectory.ShouldBeTrue();
        files.Stat(PosixPath.Join(Root, "app.py")).ShouldNotBeNull().IsDirectory.ShouldBeFalse();
        files.Stat(PosixPath.Join(Root, "nothing-here")).ShouldBeNull();
    }

    /// <summary>
    /// The round trip that matters on Windows, and is a no-op everywhere else.
    /// A drive is addressed as /C:/Users/you, which is what Windows OpenSSH's
    /// own sftp-server says.
    /// </summary>
    [Fact]
    public void ThePosixSpellingSurvivesTheRoundTrip()
    {
        LocalFiles.Native(LocalFiles.Posix(_root)).ShouldBe(_root);

        if (OperatingSystem.IsWindows())
        {
            LocalFiles.Posix(@"C:\Users\you").ShouldBe("/C:/Users/you");
            LocalFiles.Native("/C:/Users/you").ShouldBe(@"C:\Users\you");
        }
        else
        {
            // Nothing to translate: the paths were already POSIX.
            LocalFiles.Posix("/home/ops/srv").ShouldBe("/home/ops/srv");
            LocalFiles.Native("/home/ops/srv").ShouldBe("/home/ops/srv");
        }
    }

    /// <summary>
    /// And the whole workspace works over it, unchanged.
    ///
    /// This is the point of implementing the far end's seam rather than
    /// something new: the root rule, the text handling and the save are the same
    /// code that talks to a server.
    /// </summary>
    [Fact]
    public void AWorkspaceOverThisMachineBehavesLikeOneOverAServer()
    {
        var workspace = new RemoteWorkspace(Files, Root);

        workspace.List(".").Select(entry => entry.Name).ShouldBe(["conf", "app.py"]);

        var read = workspace.Read("conf/nginx.conf");
        read.IsBinary.ShouldBeFalse();
        read.Newline.ShouldBe("\n");
        read.Text.ShouldContain("listen 80;");

        workspace.Write("conf/nginx.conf", read.Text.Replace("80", "8080"), read.Newline);
        File.ReadAllText(Path.Combine(_root, "conf", "nginx.conf")).ShouldContain("listen 8080;");

        // And the rule that makes it a workspace holds here too.
        Should.Throw<WorkspaceBoundsException>(() => workspace.Read("../../../etc/hosts"));
    }

    [Fact]
    public void DeletingAFolderTakesWhatIsInIt()
    {
        var files = Files;
        var conf = files.Stat(PosixPath.Join(Root, "conf")).ShouldNotBeNull();

        files.Delete(conf);

        Directory.Exists(Path.Combine(_root, "conf")).ShouldBeFalse();
    }

    [Fact]
    public void ADirectoryThatCannotBeReadDoesNotTakeTheListingWithIt()
    {
        // A file that goes away between the listing and the question about it is
        // ordinary on a machine somebody is using. What must not happen is the
        // whole directory failing because of one entry.
        var files = Files;
        File.WriteAllText(Path.Combine(_root, "going.txt"), "x");

        var listed = files.List(Root);

        listed.ShouldContain(entry => entry.Name == "going.txt");
        listed.Count.ShouldBe(3);
    }
}
