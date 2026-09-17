using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// Everything the window asks a host for goes through one seam.
///
/// The seam is what makes one authenticated session per host possible: behind it
/// is <c>ConnectionPool</c>, which has had its own tests since M2 and which
/// nothing used until now. What these check is that the window asks it rather
/// than opening its own connections, which is how four of them happened.
/// </summary>
public class HostSessionTests
{
    private sealed class Files : Transport.IRemoteFiles
    {
        public string Home => "/home/ops";

        public IReadOnlyList<Transport.RemoteEntry> List(string path) => [];

        public void Download(string remotePath, string localPath) { }

        public void Upload(string localPath, string remotePath) { }

        public void Delete(Transport.RemoteEntry entry) { }

        public void Rename(string path, string newPath) { }

        public void CreateDirectory(string path) { }

        public Transport.RemoteEntry? Stat(string path) => null;

        public byte[] Read(string path, long limit) => [];

        public void Write(string path, byte[] content) { }
    }

    private sealed class Tunnels : Transport.ITunnels
    {
        public Transport.IRunningTunnel Start(PortForward forward) => throw new NotSupportedException();
    }

    private static (ShellViewModel Shell, FakeSessions Sessions, Connection Host) Arrange()
    {
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var sessions = new FakeSessions
        {
            OnFiles = _ => new Files(),
            OnTunnels = _ => new Tunnels(),
        };
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            sessions,
            (_, _) => new Border());
        shell.Inventory.Selection = host.Id;
        return (shell, sessions, host);
    }

    [Fact]
    public async Task AShellAFileBrowserAndTunnelsAllAskTheSameThing()
    {
        var (shell, sessions, _) = Arrange();

        await shell.ConnectSelectedCommand.ExecuteAsync(null);
        await shell.BrowseFilesCommand.ExecuteAsync(null);
        await shell.OpenTunnelsCommand.ExecuteAsync(null);

        sessions.Asked.ShouldBe(["health web-01", "shell web-01", "files web-01", "tunnels web-01"]);
    }

    [Fact]
    public void TheDashboardAsksForItsOwnHealthAndNothingElse()
    {
        // Built when a host is selected, connected only when someone presses
        // Check: the health handed out is lazy for exactly that reason.
        var (_, sessions, _) = Arrange();

        sessions.Asked.ShouldBe(["health web-01"]);
    }

    [Fact]
    public void SelectingASecondHostDoesNotDisturbTheFirst()
    {
        var first = new Connection { Name = "web-01", Hostname = "web-01.example.com" };
        var second = new Connection { Name = "db-01", Hostname = "db-01.example.com" };
        var sessions = new FakeSessions();
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [first, second])),
            sessions,
            (_, _) => new Border());

        shell.Inventory.Selection = first.Id;
        shell.Inventory.Selection = second.Id;
        shell.Inventory.Selection = first.Id;

        // One per host, and the first host's dashboard was kept rather than
        // rebuilt, so it was not asked for twice.
        sessions.Asked.ShouldBe(["health web-01", "health db-01"]);
    }
}
