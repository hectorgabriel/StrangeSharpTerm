using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.Terminal;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // One host from the command line until the shell and its inventory
            // arrive in M4. The window is the whole app for now, which is enough
            // to run a terminal and watch it behave.
            var request = TerminalLaunchRequest.Parse(desktop.Args ?? []);
            desktop.MainWindow = request is null ? new MainWindow() : TerminalWindow(request);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window TerminalWindow(TerminalLaunchRequest request)
    {
        var window = new Window { Title = $"StrangeSharpTerm — {request.Target}", Width = 900, Height = 560 };
        try
        {
            var session = TerminalLauncher.Connect(request);
            var pane = new TerminalPaneView(session, TerminalPalette.ByName(request.Theme), new TerminalRegistry());
            pane.TitleChanged += (_, title) => window.Title = title.Length > 0 ? title : window.Title;
            pane.Ended += (_, _) => window.Title = $"{window.Title} — disconnected";
            window.Content = pane;
        }
        catch (Exception e)
        {
            // A connection that fails has to say why in the window: there is no
            // other surface yet to say it on.
            window.Content = new TextBlock
            {
                Margin = new Thickness(24),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = SshFailure.Classify(e).Summary,
            };
        }
        return window;
    }
}