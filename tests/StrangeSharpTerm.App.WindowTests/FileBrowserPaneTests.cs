using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The file browser as a pane: drawn, and sitting where a pane sits.
/// </summary>
[Collection("window")]
public class FileBrowserPaneTests
{
    /// <summary>Two directories and a file, without a server.</summary>
    private sealed class FakeFiles : IRemoteFiles
    {
        public string Home => "/home/ops";

        public IReadOnlyList<RemoteEntry> List(string path) =>
        [
            new("logs", "/home/ops/logs", true, false, 4096, new DateTime(2026, 9, 1, 10, 0, 0)),
            new("notes.txt", "/home/ops/notes.txt", false, false, 2048, new DateTime(2026, 9, 2, 11, 30, 0)),
        ];

        public void Download(string remotePath, string localPath) { }

        public void Upload(string localPath, string remotePath) { }

        public void Delete(RemoteEntry entry) { }

        public void Rename(string path, string newPath) { }

        public void CreateDirectory(string path) { }

        public RemoteEntry? Stat(string path) => null;

        public byte[] Read(string path, long limit) => [];

        public void Write(string path, byte[] content) { }
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    [Fact]
    public void ItDrawsWhatTheDirectoryHolds()
    {
        Headless.Run(() =>
        {
            var model = new FileBrowserViewModel(new FakeFiles(), "web-01");
            var view = new FileBrowserView(model);
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();
            Headless.Finish(model.RefreshCommand.ExecuteAsync(null));
            Settle(window);

            var shown = In<TextBlock>(window).Select(block => block.Text).ToArray();
            shown.ShouldContain("/home/ops");
            shown.ShouldContain("logs");
            shown.ShouldContain("notes.txt");
            // A size for the file, nothing for the directory.
            shown.ShouldContain("2 KB");
        });
    }

    [Fact]
    public void AnEmptyDirectorySaysSoRatherThanLookingBroken()
    {
        Headless.Run(() =>
        {
            var model = new FileBrowserViewModel(new EmptyFiles(), "web-01");
            var view = new FileBrowserView(model);
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();
            Headless.Finish(model.RefreshCommand.ExecuteAsync(null));
            Settle(window);

            In<TextBlock>(window).First(block => block.Text == "Nothing here.").IsEffectivelyVisible.ShouldBeTrue();
        });
    }

    private sealed class EmptyFiles : IRemoteFiles
    {
        public string Home => "/tmp/empty";

        public IReadOnlyList<RemoteEntry> List(string path) => [];

        public void Download(string remotePath, string localPath) { }

        public void Upload(string localPath, string remotePath) { }

        public void Delete(RemoteEntry entry) { }

        public void Rename(string path, string newPath) { }

        public void CreateDirectory(string path) { }

        public RemoteEntry? Stat(string path) => null;

        public byte[] Read(string path, long limit) => [];

        public void Write(string path, byte[] content) { }
    }

    [Fact]
    public void BrowsingOpensAPaneBesideTheShell()
    {
        Headless.Run(() =>
        {
            var host = new Model.Connection { Name = "web-01", Hostname = "web-01.example.com" };
            var model = new ShellViewModel(
                new InventoryViewModel(null, new Model.InventoryTree(connections: [host])),
                new StrangeSharpTerm.App.Tests.FakeSessions { OnFiles = _ => new FakeFiles() });
            var window = new ShellWindow(model) { Width = 1100, Height = 700 };
            window.Show();
            Settle(window);

            model.Inventory.Selection = host.Id;
            Settle(window);
            Headless.Finish(model.BrowseFilesCommand.ExecuteAsync(null));
            Settle(window);

            // A pane in a tab, like a terminal: that is the whole point of it
            // being a pane rather than a window.
            model.Tabs.ShouldHaveSingleItem().Title.ShouldBe("web-01 files");
            In<FileBrowserView>(window).Count().ShouldBe(1);
            In<TextBlock>(window).Select(block => block.Text).ShouldContain("notes.txt");
        });
    }
}
