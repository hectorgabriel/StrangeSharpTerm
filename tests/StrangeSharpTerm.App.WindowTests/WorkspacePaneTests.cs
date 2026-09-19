using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.Tests;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The workspace as a pane: drawn, with a tree on one side and the file being
/// edited on the other.
///
/// Here rather than in a plain test because a tree view and a text box both need
/// templates applied and a layout run before there is anything to assert on.
/// </summary>
[Collection("window")]
public class WorkspacePaneTests
{
    private static MemoryFiles Project() => new MemoryFiles()
        .With("/home/ops/srv/app/app.py", "print('one')\nprint('two')\n")
        .With("/home/ops/srv/app/conf/nginx.conf", "server {\n  listen 80;\n}\n");

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    [Fact]
    public void ItDrawsTheFolderAsATree()
    {
        Headless.Run(() =>
        {
            var model = new HostWorkspaceViewModel(Project(), "web-01", "/home/ops/srv/app");
            var window = new Window { Content = new WorkspaceView(model), Width = 900, Height = 600 };
            window.Show();
            Headless.Finish(model.RefreshCommand.ExecuteAsync(null));
            Settle(window);

            var shown = In<TextBlock>(window).Select(block => block.Text).ToArray();
            shown.ShouldContain("conf");
            shown.ShouldContain("app.py");
        });
    }

    [Fact]
    public void AnOpenFileIsInTheEditorWithItsLineNumbersBesideIt()
    {
        Headless.Run(() =>
        {
            var model = new HostWorkspaceViewModel(Project(), "web-01", "/home/ops/srv/app");
            var window = new Window { Content = new WorkspaceView(model), Width = 900, Height = 600 };
            window.Show();
            Headless.Finish(model.OpenFile("conf/nginx.conf"));
            Settle(window);

            var editor = In<TextBox>(window).First(box => box.Name == "Editor");
            editor.Text.ShouldBe("server {\n  listen 80;\n}\n");

            // The numbers are one block of text, not a control per line: a
            // control per line is a thousand of them in a thousand-line file.
            In<TextBlock>(window).First(block => block.Name == "Gutter").Text.ShouldBe("1\n2\n3\n4");
        });
    }

    [Fact]
    public void TypingInTheEditorIsWhatMakesSavingPossible()
    {
        Headless.Run(() =>
        {
            var model = new HostWorkspaceViewModel(Project(), "web-01", "/home/ops/srv/app");
            var window = new Window { Content = new WorkspaceView(model), Width = 900, Height = 600 };
            window.Show();
            Headless.Finish(model.OpenFile("app.py"));
            Settle(window);

            model.CanSave.ShouldBeFalse();

            var editor = In<TextBox>(window).First(box => box.Name == "Editor");
            editor.Text += "print('three')\n";
            Settle(window);

            // Through the binding rather than through the view model, which is
            // the half a plain test cannot check: a text box bound one way
            // would show the file and save nothing.
            model.Current.ShouldNotBeNull().IsDirty.ShouldBeTrue();
            model.CanSave.ShouldBeTrue();
            In<TextBlock>(window).Select(block => block.Text).ShouldContain("app.py •");
        });
    }

    [Fact]
    public void WithNothingOpenItSaysWhatTheFolderIsFor()
    {
        Headless.Run(() =>
        {
            var model = new HostWorkspaceViewModel(Project(), "web-01", "/home/ops/srv/app");
            var window = new Window { Content = new WorkspaceView(model), Width = 900, Height = 600 };
            window.Show();
            Settle(window);

            In<TextBlock>(window)
                .First(block => block.Text?.StartsWith("Open a file from the tree") == true)
                .IsEffectivelyVisible.ShouldBeTrue();
        });
    }

    /// <summary>
    /// A host's folder is the pane's, not the fleet's -- in a plan as well as in
    /// Ask mode.
    ///
    /// A plan's workers were built with the host's folder, so what a phase could
    /// read depended on which hosts somebody happened to have a folder open on.
    /// </summary>
    [Fact]
    public void APlansWorkersAreNotGivenTheHostsFolder()
    {
        Headless.Run(() =>
        {
            var host = new StrangeSharpTerm.Model.Connection { Name = "web-01", Hostname = "web-01.example.com" };
            var backend = new Recording(
                Canned.Says("""{"phases":[{"name":"Look","hosts":["web-01"],"commands":["uptime"]}]}"""),
                Canned.Says("Done."));
            var shell = new ShellViewModel(
                new InventoryViewModel(null, new StrangeSharpTerm.Model.InventoryTree(connections: [host])),
                new FakeSessions { OnFiles = _ => Project() },
                (_, _) => new Border(),
                backends: _ => backend,
                orchestratorView: model => new Border { DataContext = model },
                assistantView: model => new Border { DataContext = model },
                workspaceView: model => new Border { DataContext = model });
            shell.Inventory.Selection = host.Id;
            Headless.Finish(shell.OpenWorkspaceCommand.ExecuteAsync(null));

            shell.AskSeveralHostsCommand.Execute(null);
            var model = (OrchestratorViewModel)shell.Dock!.DataContext!;
            model.Targets.Single().IsChosen = true;
            model.Mode = OrchestratorMode.Plan;
            model.Instruction = "look at it";
            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Dispatcher.UIThread.RunJobs();
            model.Phases.ShouldHaveSingleItem();

            Headless.Finish(model.RunCommand.ExecuteAsync(null));
            Dispatcher.UIThread.RunJobs();

            // The worker was asked, and offered nothing about the host's files.
            backend.Requests.Count.ShouldBe(2);
            var worker = backend.Requests[1];
            worker.Tools.Select(tool => tool.Name).ShouldNotContain(StrangeSharpTerm.Assist.WorkspaceTools.ReadFile);
            worker.Tools.Select(tool => tool.Name).ShouldNotContain(StrangeSharpTerm.Assist.WorkspaceTools.ListFiles);
        });
    }

    private sealed class Recording(params IReadOnlyList<StrangeSharpTerm.Assist.AssistEvent>[] turns)
        : StrangeSharpTerm.Assist.IAssistBackend
    {
        private readonly Canned _canned = new(turns);

        internal List<StrangeSharpTerm.Assist.AssistRequest> Requests { get; } = [];

        public string ProviderName => _canned.ProviderName;

        public string Model => _canned.Model;

        public IAsyncEnumerable<StrangeSharpTerm.Assist.AssistEvent> Stream(
            StrangeSharpTerm.Assist.AssistRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _canned.Stream(request, cancellationToken);
        }
    }
}
