using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The file browser, against a server that is a dictionary.
///
/// SFTP is a network protocol and the decisions are not: what order to show,
/// what to do with a double click, where a download lands, and what happens when
/// the far end says no. Those are what these are about.
/// </summary>
public class FileBrowserTests
{
    /// <summary>A filesystem in memory, with a way to make any call fail.</summary>
    private sealed class FakeFiles : IRemoteFiles
    {
        private readonly Dictionary<string, List<RemoteEntry>> _tree = [];

        public string Home { get; set; } = "/home/ops";

        public List<string> Did { get; } = [];

        /// <summary>Thrown by the next call, whatever it is.</summary>
        public Exception? Refuses { get; set; }

        public FakeFiles With(string path, params RemoteEntry[] entries)
        {
            _tree[path] = [.. entries];
            return this;
        }

        public static RemoteEntry Dir(string parent, string name) =>
            new(name, FileBrowserViewModel.Join(parent, name), true, false, 4096, new DateTime(2026, 9, 1, 10, 0, 0));

        public static RemoteEntry File(string parent, string name, long length = 1024) =>
            new(name, FileBrowserViewModel.Join(parent, name), false, false, length, new DateTime(2026, 9, 2, 11, 30, 0));

        public IReadOnlyList<RemoteEntry> List(string path)
        {
            Check($"list {path}");
            return _tree.TryGetValue(path, out var entries) ? entries : [];
        }

        public void Download(string remotePath, string localPath) => Check($"download {remotePath} -> {localPath}");

        public void Upload(string localPath, string remotePath) => Check($"upload {localPath} -> {remotePath}");

        public void Delete(RemoteEntry entry) => Check($"delete {entry.Path}");

        public void Rename(string path, string newPath) => Check($"rename {path} -> {newPath}");

        public void CreateDirectory(string path) => Check($"mkdir {path}");

        private void Check(string what)
        {
            Did.Add(what);
            if (Refuses is { } refusal)
            {
                Refuses = null;
                throw refusal;
            }
        }
    }

    /// <summary>Runs the "off thread" work on this one, so a test needs no waiting.</summary>
    private static Task Here(Action work)
    {
        work();
        return Task.CompletedTask;
    }

    private static FileBrowserViewModel Browser(FakeFiles files, IDialogService? dialogs = null) =>
        new(files, "web-01", dialogs, Here);

    [Fact]
    public async Task ItStartsInTheAccountsOwnDirectory()
    {
        var files = new FakeFiles()
            .With("/home/ops", FakeFiles.File("/home/ops", "notes.txt"));
        var browser = Browser(files);

        await browser.Refresh();

        browser.Path.ShouldBe("/home/ops");
        browser.Rows.ShouldHaveSingleItem().Name.ShouldBe("notes.txt");
    }

    [Fact]
    public async Task DirectoriesComeFirstAndThenItIsAlphabetical()
    {
        // The order every file manager uses, and the one that makes a deep tree
        // navigable by eye.
        var files = new FakeFiles().With(
            "/home/ops",
            FakeFiles.File("/home/ops", "zebra.log"),
            FakeFiles.Dir("/home/ops", "var"),
            FakeFiles.File("/home/ops", "Alpha.conf"),
            FakeFiles.Dir("/home/ops", "etc"));
        var browser = Browser(files);

        await browser.Refresh();

        browser.Rows.Select(row => row.Name).ShouldBe(["etc", "var", "Alpha.conf", "zebra.log"]);
    }

    [Fact]
    public async Task OpeningADirectoryGoesIntoIt()
    {
        var files = new FakeFiles()
            .With("/home/ops", FakeFiles.Dir("/home/ops", "logs"))
            .With("/home/ops/logs", FakeFiles.File("/home/ops/logs", "syslog"));
        var browser = Browser(files);
        await browser.Refresh();

        await browser.OpenCommand.ExecuteAsync(browser.Rows[0]);

        browser.Path.ShouldBe("/home/ops/logs");
        browser.Rows.ShouldHaveSingleItem().Name.ShouldBe("syslog");
    }

    [Fact]
    public async Task OpeningAFileDoesNothingAtAll()
    {
        // Not an error and not a download: a double click on a file is a
        // question this pane has no answer for yet.
        var files = new FakeFiles().With("/home/ops", FakeFiles.File("/home/ops", "notes.txt"));
        var browser = Browser(files);
        await browser.Refresh();

        await browser.OpenCommand.ExecuteAsync(browser.Rows[0]);

        browser.Path.ShouldBe("/home/ops");
        browser.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task GoingUpStopsAtTheRoot()
    {
        var files = new FakeFiles { Home = "/home/ops" };
        var browser = Browser(files);
        await browser.Refresh();

        await browser.GoUpCommand.ExecuteAsync(null);
        browser.Path.ShouldBe("/home");

        await browser.GoUpCommand.ExecuteAsync(null);
        browser.Path.ShouldBe("/");
        browser.CanGoUp.ShouldBeFalse();

        await browser.GoUpCommand.ExecuteAsync(null);
        browser.Path.ShouldBe("/");
    }

    [Fact]
    public void APathIsARemotePathOnEveryPlatform()
    {
        // System.IO.Path would turn these into Windows paths on Windows, which
        // is the wrong answer for a POSIX server reached from either.
        FileBrowserViewModel.Join("/var/log", "syslog").ShouldBe("/var/log/syslog");
        FileBrowserViewModel.Join("/", "etc").ShouldBe("/etc");
        FileBrowserViewModel.Parent("/var/log/syslog").ShouldBe("/var/log");
        FileBrowserViewModel.Parent("/var").ShouldBe("/");
        FileBrowserViewModel.Parent("/").ShouldBeNull();
    }

    [Fact]
    public async Task HomeGoesBackThereFromAnywhere()
    {
        var files = new FakeFiles { Home = "/home/ops" };
        var browser = Browser(files);
        await browser.Load("/var/log");

        await browser.GoHomeCommand.ExecuteAsync(null);

        browser.Path.ShouldBe("/home/ops");
    }

    [Fact]
    public async Task ARefusalIsSaidInThePaneRatherThanThrown()
    {
        // A file browser is one long argument with a network. A pane that throws
        // when a directory cannot be read is a pane that disappears.
        var files = new FakeFiles { Refuses = new Renci.SshNet.Common.SftpPermissionDeniedException("nope") };
        var browser = Browser(files);

        await browser.Load("/root");

        browser.Failure.ShouldNotBeNull().ShouldContain("permission denied");
        // And it did not move: the directory that could not be read is not the
        // one on screen.
        browser.Path.ShouldBe("/home/ops");
        browser.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task TheNextThingThatWorksClearsTheComplaint()
    {
        var files = new FakeFiles { Refuses = new Renci.SshNet.Common.SftpPathNotFoundException("gone") }
            .With("/home/ops", FakeFiles.File("/home/ops", "notes.txt"));
        var browser = Browser(files);
        await browser.Load("/nowhere");
        browser.Failure.ShouldNotBeNull();

        await browser.Refresh();

        browser.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadingSaysWhereItWent()
    {
        var files = new FakeFiles().With("/home/ops", FakeFiles.File("/home/ops", "notes.txt"));
        var browser = Browser(files);
        await browser.Refresh();
        browser.Selected = browser.Rows[0];

        await browser.DownloadCommand.ExecuteAsync(null);

        files.Did.ShouldContain(did => did.StartsWith("download /home/ops/notes.txt", StringComparison.Ordinal));
        browser.Note.ShouldNotBeNull().ShouldContain("notes.txt");
    }

    [Fact]
    public async Task ADirectoryIsNotDownloaded()
    {
        var files = new FakeFiles().With("/home/ops", FakeFiles.Dir("/home/ops", "logs"));
        var browser = Browser(files);
        await browser.Refresh();
        browser.Selected = browser.Rows[0];

        await browser.DownloadCommand.ExecuteAsync(null);

        files.Did.ShouldNotContain(did => did.StartsWith("download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UploadingPutsThemWhereYouAreLooking()
    {
        var files = new FakeFiles().With("/var/www", FakeFiles.File("/var/www", "index.html"));
        var dialogs = new ScriptedDialogService { Files = ["/tmp/one.txt", "/tmp/two.txt"] };
        var browser = Browser(files, dialogs);
        await browser.Load("/var/www");

        await browser.UploadCommand.ExecuteAsync(null);

        files.Did.ShouldContain("upload /tmp/one.txt -> /var/www/one.txt");
        files.Did.ShouldContain("upload /tmp/two.txt -> /var/www/two.txt");
        browser.Note.ShouldNotBeNull().ShouldContain("2 files");
        // And the directory is read again, so the new files are on screen.
        files.Did.Count(did => did == "list /var/www").ShouldBe(2);
    }

    [Fact]
    public async Task PickingNothingUploadsNothing()
    {
        var files = new FakeFiles();
        var browser = Browser(files, new ScriptedDialogService());
        await browser.Refresh();
        files.Did.Clear();

        await browser.UploadCommand.ExecuteAsync(null);

        files.Did.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingAsksFirstAndSaysWhatGoesWithIt()
    {
        var files = new FakeFiles().With("/home/ops", FakeFiles.Dir("/home/ops", "logs"));
        var dialogs = new ScriptedDialogService(answer: false);
        var browser = Browser(files, dialogs);
        await browser.Refresh();
        browser.Selected = browser.Rows[0];

        await browser.DeleteCommand.ExecuteAsync(null);

        dialogs.Asked.ShouldContain(asked => asked.Title == "Delete logs?");
        dialogs.Asked.ShouldContain(asked => asked.Detail.Contains("Everything inside it"));
        files.Did.ShouldNotContain(did => did.StartsWith("delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SayingYesDeletesItAndReadsTheDirectoryAgain()
    {
        var files = new FakeFiles().With("/home/ops", FakeFiles.File("/home/ops", "old.log"));
        var browser = Browser(files, new ScriptedDialogService(answer: true));
        await browser.Refresh();
        browser.Selected = browser.Rows[0];

        await browser.DeleteCommand.ExecuteAsync(null);

        files.Did.ShouldContain("delete /home/ops/old.log");
        files.Did.Count(did => did == "list /home/ops").ShouldBe(2);
    }

    [Fact]
    public async Task MovingSomewhereElseForgetsTheSelection()
    {
        // Otherwise Delete would act on a row that is no longer on screen.
        var files = new FakeFiles()
            .With("/home/ops", FakeFiles.Dir("/home/ops", "logs"), FakeFiles.File("/home/ops", "notes.txt"))
            .With("/home/ops/logs");
        var browser = Browser(files);
        await browser.Refresh();
        browser.Selected = browser.Rows[1];

        await browser.OpenCommand.ExecuteAsync(browser.Rows[0]);

        browser.Selected.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void ASizeIsSaidTheWayAFileManagerSaysIt(long length, string expected) =>
        FileRow.Bytes(length).ShouldBe(expected);

    [Fact]
    public void ADirectoryHasNoSizeWorthShowing()
    {
        // Its own entry is 4096 bytes on most filesystems, which is true and
        // tells nobody anything.
        new FileRow(FakeFiles.Dir("/home/ops", "logs")).Size.ShouldBe("");
        new FileRow(FakeFiles.File("/home/ops", "notes.txt", 2048)).Size.ShouldBe("2 KB");
    }

    [Fact]
    public async Task ABrowserOpensBesideTheSessionItIsAbout()
    {
        // The reason it is a pane and not a window: a directory listing next to
        // the shell that is about to act on it.
        var host = new Model.Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new Model.InventoryTree(connections: [host])),
            new FakeSessions { OnFiles = _ => new FakeFiles() },
            (_, _) => new Avalonia.Controls.Border());
        shell.Inventory.Selection = host.Id;
        await shell.ConnectSelectedCommand.ExecuteAsync(null);

        await shell.BrowseFilesCommand.ExecuteAsync(null);

        shell.Tabs.ShouldHaveSingleItem();
        shell.Panes.Count.ShouldBe(2);
        shell.Panes[^1].IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task WithNothingOpenItGetsATabOfItsOwn()
    {
        var host = new Model.Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new Model.InventoryTree(connections: [host])),
            new FakeSessions { OnFiles = _ => new FakeFiles() });
        shell.Inventory.Selection = host.Id;

        await shell.BrowseFilesCommand.ExecuteAsync(null);

        shell.Tabs.ShouldHaveSingleItem().Title.ShouldBe("web-01 files");
        shell.Panes.ShouldHaveSingleItem();
    }

    /// <summary>A channel that carries nothing, so a session needs no server.</summary>
    private sealed class DeadChannel : StrangeSharpTerm.Terminal.ITerminalChannel
    {
        public Stream Stream { get; } = new MemoryStream();

        public void Resize(int columns, int rows) { }

        public void Dispose() => Stream.Dispose();
    }

    [Fact]
    public void AnEntrySaysWhichIconItWants()
    {
        new FileRow(FakeFiles.Dir("/", "etc")).IconKey.ShouldBe("IconFolder");
        new FileRow(FakeFiles.File("/", "notes.txt")).IconKey.ShouldBe("IconFile");
        new FileRow(new RemoteEntry("link", "/link", false, true, 0, DateTime.UnixEpoch))
            .IconKey.ShouldBe("IconArrowRightToLine");
    }
}
