using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>The server card in the detail pane, drawn.</summary>
[Collection("window")]
public class DashboardCardTests
{
    private sealed class FakeHealth : IServerHealth
    {
        public int Collected { get; private set; }

        public ServerMetrics Collect()
        {
            Collected++;
            return new ServerMetrics
            {
                Uptime = "up 12 days, 4:31",
                LoadAverages = [0.84, 1.12, 0.97],
                MemoryUsedBytes = 6_453_000_000,
                MemoryTotalBytes = 18_030_000_000,
                DiskUsedBytes = 44_270_000_000,
                DiskTotalBytes = 56_600_000_000,
                Containers = [new ServerMetrics.Container("postgres", "Up 12 days")],
            };
        }
    }

    private static (ShellWindow Window, ShellViewModel Model, FakeHealth Health) Open()
    {
        var health = new FakeHealth();
        var host = new Connection { Name = "db-01", Hostname = "db-01.example.com" };
        var model = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [host])),
            health: _ => health);
        var window = new ShellWindow(model) { Width = 1100, Height = 700 };
        window.Show();
        model.Inventory.Selection = host.Id;
        Settle(window);
        return (window, model, health);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    private static string[] Words(Visual root) =>
        [.. In<TextBlock>(root).Select(block => block.Text).OfType<string>()];

    [Fact]
    public void SelectingAHostDrawsTheCardWithoutAskingTheServerAnything()
    {
        Headless.Run(() =>
        {
            var (window, _, health) = Open();

            Words(window).ShouldContain("SERVER");
            Words(window).ShouldContain(text => text.StartsWith("Uptime, load, memory"));
            health.Collected.ShouldBe(0);
        });
    }

    [Fact]
    public void CheckingFillsItIn()
    {
        Headless.Run(() =>
        {
            var (window, model, health) = Open();

            Headless.Finish(model.Detail!.Dashboard!.RefreshCommand.ExecuteAsync(null));
            Settle(window);

            health.Collected.ShouldBe(1);
            var words = Words(window);
            words.ShouldContain("up 12 days, 4:31");
            words.ShouldContain("0.84  1.12  0.97");
            words.ShouldContain("6.01 GB / 16.79 GB");
            words.ShouldContain("postgres");
            words.ShouldContain("Up 12 days");
        });
    }

    [Fact]
    public void TheLineNeedsTwoPointsBeforeItIsALine()
    {
        Headless.Run(() =>
        {
            var (window, model, _) = Open();
            var dashboard = model.Detail!.Dashboard!;

            Headless.Finish(dashboard.RefreshCommand.ExecuteAsync(null));
            Settle(window);
            dashboard.LoadHistory.Count.ShouldBe(1);

            Headless.Finish(dashboard.RefreshCommand.ExecuteAsync(null));
            Settle(window);

            // A new list each time, because the sparkline is bound to it and a
            // binding handed the same instance twice redraws nothing.
            var line = In<Sparkline>(window).ShouldHaveSingleItem();
            line.Values.ShouldNotBeNull().Count.ShouldBe(2);
            line.Bounds.Width.ShouldBeGreaterThan(0);
        });
    }
}
