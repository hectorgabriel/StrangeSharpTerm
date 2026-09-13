using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The window itself, drawn: the markup binds, the styles resolve, and the
/// theme reaches real controls.
///
/// A view-model test can say the sidebar has three rows. Only this can say the
/// window drew three buttons, that clicking one selected a host, and that a
/// text field is the colour the theme says — which is the class of thing that
/// has gone wrong in every UI change so far.
/// </summary>
[Collection("window")]
public class ShellWindowTests
{
    /// <summary>
    /// A shell that has said nothing yet.
    ///
    /// Not an empty MemoryStream: a read of one returns zero immediately, which
    /// the terminal control reads as end-of-stream and asks again, and the test
    /// run never finishes. A real channel blocks until there is something to
    /// say, so this does too.
    /// </summary>
    private sealed class QuietChannel : ITerminalChannel
    {
        private readonly Quiet _stream = new();

        public Stream Stream => _stream;

        public void Resize(int columns, int rows) { }

        public void Dispose() => _stream.Dispose();

        private sealed class Quiet : Stream
        {
            private readonly ManualResetEventSlim _closed = new();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                _closed.Wait();
                return 0;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                await Task.Run(() => _closed.Wait(token), token);
                return 0;
            }

            /// <summary>Keystrokes go nowhere, which is what a quiet shell does with them.</summary>
            public override void Write(byte[] buffer, int offset, int count) { }

            public override void Write(ReadOnlySpan<byte> buffer) { }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                _closed.Set();
                base.Dispose(disposing);
            }
        }
    }

    private static (ShellWindow Window, ShellViewModel Model, Connection Host) Open(AppTheme? theme = null)
    {
        var folder = new Model.Folder { Name = "Production" };
        var host = new Connection { ParentId = folder.Id, Name = "web-01", Hostname = "web-01.example.com" };
        var model = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree([folder], [host])),
            new StrangeSharpTerm.App.Tests.FakeSessions { OnShell = _ => new TerminalSession(new QuietChannel()) },
            // A stand-in for the terminal control, not the control itself:
            // Iciclecreek's renderer never settles under the headless platform
            // and the run does not finish. What these tests are about is the
            // window around a pane — where it sits, what colour it is, whether
            // it survives a split — and a Border answers all three. The control
            // itself is checked by running the app (docs/handoff.md).
            (_, _) => new Border { Background = Brushes.Black },
            theme: theme);
        var window = new ShellWindow(model) { Width = 1100, Height = 700 };
        window.Show();
        Settle(window);
        return (window, model, host);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    private static TextBlock? Text(Visual root, string text) =>
        In<TextBlock>(root).FirstOrDefault(block => block.Text == text);

    [Fact]
    public void TheSidebarDrawsWhatTheInventoryHolds()
    {
        Headless.Run(() =>
        {
            var (window, _, _) = Open();

            Text(window, "Production").ShouldNotBeNull();
            Text(window, "web-01").ShouldNotBeNull();
            Text(window, "web-01.example.com").ShouldNotBeNull();
            // Nothing chosen yet, so the right-hand side says so.
            Text(window, "Choose a host on the left.")?.IsVisible.ShouldBeTrue();
        });
    }

    [Fact]
    public void ClickingAHostSelectsItAndShowsWhatItWouldUse()
    {
        Headless.Run(() =>
        {
            var (window, model, host) = Open();
            var row = In<Button>(window).First(button => button.DataContext is SidebarRow { IsFolder: false });

            var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);

            model.Inventory.Selection.ShouldBe(host.Id);
            model.ShowsDetail.ShouldBeTrue();
            // The detail pane, drawn: the resolved address, not the stored one.
            Text(window, "web-01.example.com").ShouldNotBeNull();
            Text(window, "Open a shell").ShouldNotBeNull();
        });
    }

    [Fact]
    public void TheThemeReachesTheChromeAndTheControlsInIt()
    {
        Headless.Run(() =>
        {
            var theme = new AppTheme();
            theme.PaintInto(Application.Current!);
            var (window, _, _) = Open(theme);
            var search = In<TextBox>(window).First();

            Background(window).ShouldBe(ThemeTokens.ToColor(AppPalette.StrangeTermDark.Background));

            // Through the theme itself, which is what the settings sheet does.
            theme.Use(AppPalette.Dracula);
            Settle(window);

            // The window follows because its brush is a DynamicResource, and
            // nothing was rebuilt to make it happen.
            Background(window).ShouldBe(ThemeTokens.ToColor(AppPalette.Dracula.Background));

            // And Fluent's own controls follow because their keys were redefined:
            // this is the thing that was black under the pointer until ADR 0004.
            var border = In<Border>(search).First(part => part.Name == "PART_BorderElement");
            ((ISolidColorBrush)border.Background!).Color
                .ShouldBe(ThemeTokens.ToColor(AppPalette.Dracula.Surface));
        });
    }

    [Fact]
    public void OpeningAShellPutsAPaneInTheWindow()
    {
        Headless.Run(() =>
        {
            var (window, model, host) = Open();
            model.Inventory.Selection = host.Id;

            Headless.Finish(model.ConnectSelectedCommand.ExecuteAsync(null));
            Settle(window);

            Panes(window).Count.ShouldBe(1);
            model.Tabs.ShouldHaveSingleItem();

            // And splitting it puts a second one beside the first, both drawn.
            Headless.Finish(model.SplitRightCommand.ExecuteAsync(null));
            Settle(window);

            var panes = Panes(window);
            panes.Count.ShouldBe(2);
            panes[0].Bounds.Width.ShouldBeGreaterThan(0);
            // In the window's coordinates: each pane's own Bounds are relative to
            // the frame around it, where both sit at the same corner.
            Left(panes[1], window).ShouldBeGreaterThan(Left(panes[0], window));
        });
    }

    [Fact]
    public void TheToolbarWaitsUntilThereIsSomethingToActOn()
    {
        Headless.Run(() =>
        {
            var (window, _, _) = Open();

            // Split, close and broadcast act on a tab; with none open they are
            // not offered.
            var toolbar = In<ToggleButton>(window).FirstOrDefault();
            toolbar.ShouldNotBeNull();
            toolbar.GetSelfAndVisualAncestors().OfType<StackPanel>().First().IsVisible.ShouldBeFalse();
        });
    }

    private static Color Background(Visual visual) => ((ISolidColorBrush)((Window)visual).Background!).Color;

    private static double Left(Visual visual, Visual root) => visual.TranslatePoint(default, root)!.Value.X;

    /// <summary>The panes on screen: what the split view is actually holding.</summary>
    private static IReadOnlyList<Border> Panes(Visual window) =>
        [.. In<PaneSplitView>(window).SelectMany(In<Border>).Where(border => border.Classes.Contains("pane"))];
}
