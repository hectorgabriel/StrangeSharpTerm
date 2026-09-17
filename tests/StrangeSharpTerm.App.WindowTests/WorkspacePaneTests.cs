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
}
