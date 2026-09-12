using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.Terminal;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
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
            // --connect opens one host and nothing else, which is how a terminal
            // is checked against a server without the inventory in the way. With
            // no argument the app opens its own window.
            var request = TerminalLaunchRequest.Parse(desktop.Args ?? []);
            desktop.MainWindow = request is null
                ? new ShellWindow(new ShellViewModel(InventoryLoader.Load()))
                : TerminalWindow(request);
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