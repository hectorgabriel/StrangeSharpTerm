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

        // --version, before Avalonia. It exists for the packaging scripts: a
        // signed bundle can verify perfectly and still be unable to load its own
        // runtime, and this starts the host, loads the runtime and exits without
        // opening a window -- which is the whole of what those scripts need to
        // know and the only part that is awkward to check any other way.
        //
        // A WinExe has no console on Windows, so the exit code carries the
        // answer there and the text is a courtesy on the platforms that show it.
        if (Array.IndexOf(args, "--version") >= 0)
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return;
        }

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
