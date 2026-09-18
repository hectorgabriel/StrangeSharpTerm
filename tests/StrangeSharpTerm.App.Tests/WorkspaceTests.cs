using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The workspace pane: a tree, the files open in it, and what saving means.
///
/// SFTP is a network protocol and none of these are about it. What order the
/// tree reads in, what a double click does, what happens when somebody else
/// wrote to the file you have open, and what the assistant writing one does to
/// the pane showing it — those are the decisions, and none of them needs a
/// server.
/// </summary>
public class HostWorkspaceTests
{
    /// <summary>Runs the "off thread" work on this one, so a test needs no waiting.</summary>
    private static Task Here(Action work)
    {
        work();
        return Task.CompletedTask;
    }

    private static MemoryFiles Project() => new MemoryFiles()
        .With("/home/ops/srv/app/app.py", "print('one')\n")
        .With("/home/ops/srv/app/conf/nginx.conf", "server {\n  listen 80;\n}\n")
        .With("/home/ops/notes.txt", "not in the workspace\n");

    private static HostWorkspaceViewModel Workspace(
        MemoryFiles files,
        string root = "/home/ops/srv/app",
        IDialogService? dialogs = null,
        Action<string>? remember = null) =>
        new(files, "web-01", root, dialogs, Here, remember);

    [Fact]
    public async Task ItOpensAtTheFolderItWasGivenAndShowsWhatIsInIt()
    {
        var workspace = Workspace(Project());

        await workspace.RefreshCommand.ExecuteAsync(null);

        workspace.Root.ShouldBe("~/srv/app");
        // Directories first, then by name: the order a file tree reads in.
        workspace.Tree.Select(node => node.Name).ShouldBe(["conf", "app.py"]);
    }

    [Fact]
    public async Task AFolderIsReadWhenItIsOpenedAndNotBefore()
    {
        var files = Project();
        var workspace = Workspace(files);
        await workspace.RefreshCommand.ExecuteAsync(null);

        var conf = workspace.Tree.Single(node => node.Name == "conf");
        files.Did.ShouldNotContain("list /home/ops/srv/app/conf");

        conf.IsExpanded = true;
        await conf.Fill();

        conf.Children.Select(node => node.Name).ShouldBe(["nginx.conf"]);
    }

    [Fact]
    public async Task OpeningAFileGivesAnEditorAndTypingMarksItUnsaved()
    {
        var workspace = Workspace(Project());
        await workspace.OpenFile("app.py");

        var editor = workspace.Current.ShouldNotBeNull();
        editor.Text.ShouldBe("print('one')\n");
        editor.IsDirty.ShouldBeFalse();
        editor.TabTitle.ShouldBe("app.py");

        editor.Text = "print('two')\n";

        editor.IsDirty.ShouldBeTrue();
        editor.TabTitle.ShouldBe("app.py •");
        workspace.CanSave.ShouldBeTrue();
    }

    [Fact]
    public async Task SavingWritesItBackAndTheFileIsCleanAgain()
    {
        var files = Project();
        var workspace = Workspace(files);
        await workspace.OpenFile("app.py");
        workspace.Current!.Text = "print('two')\n";

        await workspace.SaveCommand.ExecuteAsync(null);

        files.Text("/home/ops/srv/app/app.py").ShouldBe("print('two')\n");
        workspace.Current!.IsDirty.ShouldBeFalse();
        workspace.Note.ShouldBe("Saved app.py");
    }

    [Fact]
    public async Task AFileThatChangedUnderneathIsNotOverwrittenWithoutAsking()
    {
        // The terminal beside this pane is on the same machine. Editing a file
        // there and saving it here is not a strange case; it is Tuesday.
        var files = Project();
        var dialogs = new ScriptedDialogService(answer: false);
        var workspace = Workspace(files, dialogs: dialogs);
        await workspace.OpenFile("app.py");
        workspace.Current!.Text = "print('mine')\n";

        files.Touch("/home/ops/srv/app/app.py", "print('theirs')\n", new DateTime(2026, 9, 4, 12, 0, 0));
        await workspace.SaveCommand.ExecuteAsync(null);

        dialogs.Asked.ShouldContain(asked => asked.Title.Contains("changed on web-01"));
        files.Text("/home/ops/srv/app/app.py").ShouldBe("print('theirs')\n");
    }

    [Fact]
    public async Task AFileOutsideTheFolderCannotBeOpenedAtAll()
    {
        var workspace = Workspace(Project());

        await workspace.OpenFile("../notes.txt");

        workspace.Open.ShouldBeEmpty();
        workspace.Failure.ShouldNotBeNull().ShouldContain("outside this workspace");
    }

    [Fact]
    public async Task ABinaryFileIsRefusedRatherThanShownAsMojibake()
    {
        var files = Project();
        files.WithBytes("/home/ops/srv/app/logo.png", 0x89, 0x50, 0x00, 0x1A);
        var workspace = Workspace(files);

        await workspace.OpenFile("logo.png");

        workspace.Open.ShouldBeEmpty();
        workspace.Failure.ShouldNotBeNull().ShouldContain("not a text file");
    }

    [Fact]
    public async Task ChangingTheFolderRemembersItAndClosesWhatWasOpen()
    {
        var files = Project();
        files.WithDirectory("/home/ops/srv/other");
        string? remembered = null;
        var workspace = Workspace(files, remember: root => remembered = root);
        await workspace.OpenFile("app.py");

        workspace.RootDraft = "~/srv/other";
        await workspace.OpenRootCommand.ExecuteAsync(null);

        workspace.Root.ShouldBe("~/srv/other");
        remembered.ShouldBe("/home/ops/srv/other");
        // The editors belonged to the folder that is no longer open.
        workspace.Open.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFolderThatIsNotThereSaysSoRatherThanOpeningNothing()
    {
        var workspace = Workspace(Project());

        workspace.RootDraft = "/srv/missing";
        await workspace.OpenRootCommand.ExecuteAsync(null);

        workspace.Root.ShouldBe("~/srv/app");
        workspace.Failure.ShouldNotBeNull().ShouldContain("not a directory");
    }

    [Fact]
    public async Task ANewFileIsCreatedWhereTheSelectionIsAndOpened()
    {
        var files = Project();
        var workspace = Workspace(files);
        await workspace.RefreshCommand.ExecuteAsync(null);
        workspace.Selected = workspace.Tree.Single(node => node.Name == "conf");

        workspace.NewFileCommand.Execute(null);
        workspace.IsNaming.ShouldBeTrue();
        workspace.NameDraft = "extra.conf";
        await workspace.ConfirmNameCommand.ExecuteAsync(null);

        files.Has("/home/ops/srv/app/conf/extra.conf").ShouldBeTrue();
        workspace.Current.ShouldNotBeNull().Relative.ShouldBe("conf/extra.conf");
        workspace.IsNaming.ShouldBeFalse();
    }

    [Fact]
    public async Task DeletingAsksFirstAndTakesTheOpenFileWithIt()
    {
        var files = Project();
        var dialogs = new ScriptedDialogService(answer: true);
        var workspace = Workspace(files, dialogs: dialogs);
        await workspace.RefreshCommand.ExecuteAsync(null);
        await workspace.OpenFile("app.py");
        workspace.Selected = workspace.Tree.Single(node => node.Name == "app.py");

        await workspace.DeleteCommand.ExecuteAsync(null);

        dialogs.Asked.ShouldContain(asked => asked.Title == "Delete app.py?");
        files.Has("/home/ops/srv/app/app.py").ShouldBeFalse();
        workspace.Open.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhenTheAssistantWritesAFileACleanEditorCatchesUp()
    {
        var files = Project();
        var workspace = Workspace(files);
        await workspace.OpenFile("app.py");

        // What the agent does, in the order it does it.
        files.Touch("/home/ops/srv/app/app.py", "print('assistant')\n", new DateTime(2026, 9, 4, 12, 0, 0));
        await workspace.Changed(new FileChange(
            "/home/ops/srv/app/app.py", "app.py", "print('assistant')\n", false, Diff.Between("", "")));

        workspace.Current.ShouldNotBeNull().Text.ShouldBe("print('assistant')\n");
        workspace.Current!.IsDirty.ShouldBeFalse();
        workspace.Note.ShouldBe("The assistant wrote app.py");
    }

    [Fact]
    public async Task WhenTheAssistantWritesAFileSomebodyIsEditingNothingIsThrownAway()
    {
        // Two writers, one file, and the pane does not get to pick a winner:
        // what is on screen is what somebody typed, and it stays.
        var files = Project();
        var workspace = Workspace(files);
        await workspace.OpenFile("app.py");
        workspace.Current!.Text = "print('mine')\n";

        files.Touch("/home/ops/srv/app/app.py", "print('assistant')\n", new DateTime(2026, 9, 4, 12, 0, 0));
        await workspace.Changed(new FileChange(
            "/home/ops/srv/app/app.py", "app.py", "print('assistant')\n", false, Diff.Between("", "")));

        workspace.Current!.Text.ShouldBe("print('mine')\n");
        workspace.Failure.ShouldNotBeNull().ShouldContain("changed by the assistant");
    }

    [Fact]
    public async Task ATruncatedFileCannotBeSavedBecauseSavingWouldTruncateIt()
    {
        var files = Project();
        files.With("/home/ops/srv/app/huge.log", new string('x', (int)RemoteWorkspace.MaxFileBytes + 10));
        var workspace = Workspace(files);

        await workspace.OpenFile("huge.log");
        workspace.Current!.Text += "more";

        workspace.CanSave.ShouldBeFalse();
        workspace.Note.ShouldNotBeNull().ShouldContain("cannot be saved");
    }
}

/// <summary>What remembering a folder does to <c>preferences.json</c>.</summary>
public class WorkspacePreferencesTests
{
    [Fact]
    public void AFolderComesBackForTheHostItWasOpenedOn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}", "preferences.json");
        var host = StrangeSharpTerm.Model.NodeId.New();
        try
        {
            Assistant.WorkspacePreferences.Save(path, host, "/home/ops/srv/app");

            Assistant.WorkspacePreferences.Load(path)[host].ShouldBe("/home/ops/srv/app");

            // And forgotten rather than remembered as empty, so the file says
            // only what is true.
            Assistant.WorkspacePreferences.Save(path, host, null);
            Assistant.WorkspacePreferences.Load(path).ShouldNotContainKey(host);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void RememberingAFolderLeavesEveryOtherPreferenceAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}", "preferences.json");
        var host = StrangeSharpTerm.Model.NodeId.New();
        try
        {
            new Preferences { Theme = "dracula" }.Save(path);

            Assistant.WorkspacePreferences.Save(path, host, "/srv/app");

            Preferences.Load(path).Theme.ShouldBe("dracula");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

/// <summary>
/// The workspace as the window arranges it: one folder per host, and the same
/// folder the conversation about that host may work in.
/// </summary>
public class WorkspacePaneTests
{
    private sealed class Answering : IRemoteCommands
    {
        public CommandResult Run(string command, TimeSpan timeout) => new(0, "Linux 6.1.0 x86_64", "");
    }

    private sealed class Quiet : IServerHealth
    {
        public ServerMetrics Collect() => new() { Uptime = "3 days" };
    }

    private static ShellViewModel Shell(MemoryFiles files, out StrangeSharpTerm.Model.Connection host)
    {
        host = new StrangeSharpTerm.Model.Connection { Name = "web-01", Hostname = "web-01.example.com" };
        return new ShellViewModel(
            new InventoryViewModel(null, new StrangeSharpTerm.Model.InventoryTree(connections: [host])),
            new FakeSessions
            {
                OnFiles = _ => files,
                OnCommands = _ => new Answering(),
                OnHealth = _ => new Quiet(),
            },
            (_, _) => new Avalonia.Controls.Border(),
            // Stand-ins, as every other pane gets here: building the real
            // control runs its XAML, and a plain test has no application to run
            // it in.
            backends: _ => new StubBackend(),
            orchestratorView: model => new Avalonia.Controls.Border { DataContext = model },
            // A stand-in for the conversation's control as well, and not only
            // for tidiness: opening a folder changes a property the real view
            // binds to a check box's IsVisible, and a binding that reaches a
            // control from a thread Avalonia's dispatcher does not own throws.
            // It threw about one run in four, in whichever test happened to
            // bind the dispatcher first.
            assistantView: model => new Avalonia.Controls.Border { DataContext = model },
            workspaceView: model => new Avalonia.Controls.Border { DataContext = model });
    }

    private static MemoryFiles Project() => new MemoryFiles()
        .With("/home/ops/srv/app/app.py", "print('one')\n");

    [Fact]
    public async Task OpeningAFolderPutsItInAPaneOfItsOwn()
    {
        var shell = Shell(Project(), out var host);
        shell.Inventory.Selection = host.Id;

        await shell.OpenWorkspaceCommand.ExecuteAsync(null);

        shell.Panes.ShouldHaveSingleItem();
        shell.Tabs.Single().Title.ShouldBe("web-01 workspace");
    }

    [Fact]
    public async Task AskingAgainBringsTheOneThatIsOpenForward()
    {
        // Two panes rooted at two folders would make "the folder open on this
        // host" a question with two answers, and that question is what the
        // assistant's file tools are judged against.
        var shell = Shell(Project(), out var host);
        shell.Inventory.Selection = host.Id;

        await shell.OpenWorkspaceCommand.ExecuteAsync(null);
        await shell.OpenWorkspaceCommand.ExecuteAsync(null);

        shell.Workspace.Panes.Count(pane => pane.Kind is PaneKind.Workspace).ShouldBe(1);
    }

    [Fact]
    public async Task TheConversationAboutThatHostGetsTheSameFolderAndLosesItWhenThePaneCloses()
    {
        var shell = Shell(Project(), out var host);
        shell.Inventory.Selection = host.Id;
        await shell.ConnectSelectedCommand.ExecuteAsync(null);
        shell.OpenAssistantCommand.Execute(null);

        // By what the control holds rather than by its type, so the stand-in
        // answers as the real control would.
        var assistant = shell.Dock?.DataContext as AssistantViewModel;
        assistant.ShouldNotBeNull().HasWorkspace.ShouldBeFalse();

        await shell.OpenWorkspaceCommand.ExecuteAsync(null);
        assistant.HasWorkspace.ShouldBeTrue();
        assistant.WorkspaceRoot.ShouldBe("~");

        // Closing the pane closes the folder, which takes the file tools with
        // it: what the assistant may touch is what is open in front of you.
        var pane = shell.Workspace.Panes.Single(pane => pane.Kind is PaneKind.Workspace);
        shell.Workspace.ClosePane(pane.Id);
        shell.FocusPaneCommand.Execute(shell.Workspace.Panes[0].Id);

        assistant.HasWorkspace.ShouldBeFalse();
    }

    /// <summary>A provider that is never actually asked anything.</summary>
    private sealed class StubBackend : IAssistBackend
    {
        public string ProviderName => "Stub";

        public string Model => "stub-1";

        public async IAsyncEnumerable<AssistEvent> Stream(
            AssistRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AssistEvent.Finished(AssistStop.EndTurn);
        }
    }
}

/// <summary>
/// This machine's files in the left panel: what the switch shows, where a file
/// opens, and the one thing the two workspaces do together.
/// </summary>
public class LocalFilesPaneTests
{
    private static MemoryFiles Project() => new MemoryFiles()
        .With("/Users/you/project/notes.md", "one\n")
        .With("/Users/you/project/src/app.py", "print()\n")
        .WithDirectory("/Users/you/other");

    private static ShellViewModel Shell(
        MemoryFiles local,
        out StrangeSharpTerm.Model.Connection host,
        MemoryFiles? remote = null,
        IDialogService? dialogs = null,
        Transport.IRemoteFiles? localFiles = null)
    {
        host = new StrangeSharpTerm.Model.Connection { Name = "web-01", Hostname = "web-01.example.com" };
        return new ShellViewModel(
            new InventoryViewModel(null, new StrangeSharpTerm.Model.InventoryTree(connections: [host])),
            new FakeSessions { OnFiles = _ => remote ?? new MemoryFiles() },
            (_, _) => new Avalonia.Controls.Border(),
            dialogs: dialogs,
            orchestratorView: model => new Avalonia.Controls.Border { DataContext = model },
            assistantView: model => new Avalonia.Controls.Border { DataContext = model },
            workspaceView: model => new Avalonia.Controls.Border { DataContext = model },
            localFiles: localFiles ?? local,
            editorView: model => new Avalonia.Controls.Border { DataContext = model });
    }

    [Fact]
    public void ThePanelStartsOnTheHostsAndNothingIsOpenHere()
    {
        var shell = Shell(Project(), out _);

        shell.SidebarShowsHosts.ShouldBeTrue();
        // Nothing until somebody chooses: the root is the whole of what this
        // app and the assistant may touch here, and nobody chose their home
        // directory.
        shell.LocalFiles.HasRoot.ShouldBeFalse();
        shell.LocalFiles.Tree.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChoosingAFolderOpensItAndRemembersIt()
    {
        var files = Project();
        var dialogs = new ScriptedDialogService { Folder = "/Users/you/project" };
        string? remembered = null;
        var shell = Shell(files, out _, dialogs: dialogs);
        shell.LocalFiles.RootChanged += (_, root) => remembered = root;

        await shell.ShowFilesCommand.ExecuteAsync(null);
        await shell.LocalFiles.ChooseFolderCommand.ExecuteAsync(null);

        shell.SidebarShowsFiles.ShouldBeTrue();
        shell.LocalFiles.HasRoot.ShouldBeTrue();
        remembered.ShouldBe("/Users/you/project");
        shell.LocalFiles.Tree.Select(node => node.Name).ShouldBe(["src", "notes.md"]);
    }

    [Fact]
    public async Task OpeningAFileFromTheSidebarPutsTheEditorWhereTheSessionsAre()
    {
        // The tree is 260 pixels wide and a line of code is not.
        var shell = Shell(Project(), out _, dialogs: new ScriptedDialogService { Folder = "/Users/you/project" });
        await shell.LocalFiles.ChooseFolderCommand.ExecuteAsync(null);

        await shell.LocalFiles.OpenFile("notes.md");

        shell.Workspace.Panes.Count(pane => pane.Kind is PaneKind.Editor).ShouldBe(1);
        shell.Tabs.Single().Title.ShouldBe("Files");

        // A second file is a tab inside it, not a second pane.
        await shell.LocalFiles.OpenFile("src/app.py");
        shell.Workspace.Panes.Count(pane => pane.Kind is PaneKind.Editor).ShouldBe(1);
        shell.LocalFiles.Open.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SavingWithTheEditorFocusedWritesThisMachinesFile()
    {
        var files = Project();
        var shell = Shell(files, out _, dialogs: new ScriptedDialogService { Folder = "/Users/you/project" });
        await shell.LocalFiles.ChooseFolderCommand.ExecuteAsync(null);
        await shell.LocalFiles.OpenFile("notes.md");

        shell.LocalFiles.Current.ShouldNotBeNull().Text = "one\ntwo\n";
        shell.CanSaveFile.ShouldBeTrue();
        await shell.SaveFileCommand.ExecuteAsync(null);

        files.Text("/Users/you/project/notes.md").ShouldBe("one\ntwo\n");
    }

    [Fact]
    public async Task AFileHereCanBeSentToTheFolderTheHostHasOpen()
    {
        // The sentence this exists for: "get this file onto that server."
        //
        // Over a real directory rather than the fake the other tests use,
        // because sending streams the file from its path on disk -- which is
        // what makes it work for a tarball as well as a note, and which a
        // dictionary standing in for a filesystem cannot exercise.
        var directory = Path.Combine(Path.GetTempPath(), $"strangesharpterm-send-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "notes.md"), "one\n");

            var remote = new MemoryFiles();
            var shell = Shell(
                new MemoryFiles(),
                out var host,
                remote,
                new ScriptedDialogService { Folder = Transport.LocalFiles.Posix(directory) },
                new Transport.LocalFiles(Transport.LocalFiles.Posix(directory)));

            await shell.LocalFiles.ChooseFolderCommand.ExecuteAsync(null);
            await shell.LocalFiles.RefreshCommand.ExecuteAsync(null);

            shell.Inventory.Selection = host.Id;
            await shell.OpenWorkspaceCommand.ExecuteAsync(null);

            // The menu item names the host it would send to, and only while
            // there is exactly one answer to "the open host".
            shell.LocalFiles.SendTarget.ShouldBe("web-01");
            shell.LocalFiles.Selected = shell.LocalFiles.Tree.Single(node => node.Name == "notes.md");
            shell.LocalFiles.CanSend.ShouldBeTrue();

            await shell.LocalFiles.SendCommand.ExecuteAsync(null);

            shell.LocalFiles.Failure.ShouldBeNull();
            remote.Text("/home/ops/notes.md").ShouldBe("one\n");
            shell.LocalFiles.Note.ShouldNotBeNull().ShouldContain("Sent notes.md to web-01");
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WithNoHostFolderOpenThereIsNowhereToSendTo()
    {
        var shell = Shell(Project(), out _);

        shell.LocalFiles.SendTarget.ShouldBeEmpty();
        shell.LocalFiles.CanSend.ShouldBeFalse();
    }
}
