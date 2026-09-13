using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>The tunnels pane, drawn.</summary>
[Collection("window")]
public class TunnelsPaneTests
{
    private sealed class FakeTunnels : ITunnels
    {
        public IRunningTunnel Start(PortForward forward) => new Running();

        private sealed class Running : IRunningTunnel
        {
            public bool IsRunning { get; private set; } = true;

            public void Stop() => IsRunning = false;

            public void Dispose() => Stop();
        }
    }

    private static PortForward Forward(string name, int bindPort, bool autoStart = false) => new()
    {
        Kind = PortForwardKind.Local,
        Name = name,
        BindPort = bindPort,
        DestinationHost = "localhost",
        DestinationPort = 5432,
        AutoStart = autoStart,
    };

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    [Fact]
    public void ItDrawsEachForwardWithWhatItDoes()
    {
        Headless.Run(() =>
        {
            var model = new TunnelsViewModel(
                new FakeTunnels(), "db-01", [Forward("postgres", 5432), Forward("http", 80)]);
            var window = new Window { Content = new TunnelsView(model), Width = 800, Height = 600 };
            window.Show();
            Settle(window);

            var shown = In<TextBlock>(window).Select(block => block.Text).ToArray();
            shown.ShouldContain("postgres");
            shown.ShouldContain("-L 5432:localhost:5432");
            // The one that cannot bind says so before anyone tries.
            shown.ShouldContain("needs root");
            shown.Count(text => text == "Start").ShouldBe(2);
        });
    }

    [Fact]
    public void StartingOneTurnsItsButtonIntoStop()
    {
        Headless.Run(() =>
        {
            var model = new TunnelsViewModel(new FakeTunnels(), "db-01", [Forward("postgres", 5432, autoStart: true)]);
            var window = new Window { Content = new TunnelsView(model), Width = 800, Height = 600 };
            window.Show();
            Settle(window);

            Headless.Finish(model.StartAutomaticCommand.ExecuteAsync(null));
            Settle(window);

            In<TextBlock>(window).Select(block => block.Text).ShouldContain("Stop");
            model.AnyRunning.ShouldBeTrue();
        });
    }

    [Fact]
    public void AHostWithNoForwardsSaysWhatOneWouldBeFor()
    {
        Headless.Run(() =>
        {
            var model = new TunnelsViewModel(new FakeTunnels(), "db-01", []);
            var window = new Window { Content = new TunnelsView(model), Width = 800, Height = 600 };
            window.Show();
            Settle(window);

            In<TextBlock>(window)
                .First(block => block.Text?.StartsWith("This host has no port forwards") == true)
                .IsEffectivelyVisible.ShouldBeTrue();
        });
    }
}
