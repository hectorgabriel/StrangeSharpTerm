using Avalonia;
using Avalonia.Logging;
using System;
using System.Diagnostics;

namespace StrangeSharpTerm.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Avalonia reports a broken binding through Trace, which a windowed app
        // sends nowhere. A binding that silently does nothing is among the
        // hardest things to notice here, so there is a way to hear about it.
        if (Environment.GetEnvironmentVariable("STRANGESHARPTERM_TRACE") is not null)
            Trace.Listeners.Add(new ConsoleTraceListener());

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace(LogEventLevel.Warning);
}
