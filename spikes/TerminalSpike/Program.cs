// Spike: drive Iciclecreek's TerminalControl from an SSH.NET ShellStream.
//
//   usage: TerminalSpike <user> <host> <port> <keyfile> [--headless]
//
// Types a command into the terminal, waits, then dumps what the emulator
// actually rendered. If the dump contains the command's output, the whole path
// works: SSH bytes -> IPtyConnection -> XTerm.NET engine -> rendered buffer.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using Renci.SshNet;
using StrangeSharpTerm.Spikes.Terminal;

var user = args[0];
var host = args[1];
var port = int.Parse(args[2]);
var keyFile = args[3];
var headless = args.Contains("--headless");

var exitCode = 1;

AppBuilder.Configure<SpikeApp>()
    .UsePlatformDetect()
    .AfterSetup(_ => SpikeApp.Configure(user, host, port, keyFile, headless, c => exitCode = c))
    .StartWithClassicDesktopLifetime(args);

return exitCode;

internal sealed class SpikeApp : Application
{
    private static string _user = "", _host = "", _key = "";
    private static int _port;
    private static bool _headless;
    private static Action<int> _report = _ => { };

    public static void Configure(string user, string host, int port, string key, bool headless, Action<int> report)
        => (_user, _host, _port, _key, _headless, _report) = (user, host, port, key, headless, report);

    public override void Initialize()
        => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var term = new TerminalControl { FontFamily = "Menlo", FontSize = 13 };
        var window = new Window
        {
            Title = "StrangeSharpTerm terminal spike",
            Width = 900,
            Height = 520,
            Content = term,
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await RunAsync(term, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunAsync(TerminalControl term, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine(ok ? $"  ok    {name}{(detail is null ? "" : $" ({detail})")}"
                                 : $"  FAIL  {name}{(detail is null ? "" : $": {detail}")}");
            if (!ok) failures++;
        }

        try
        {
            var key = new PrivateKeyFile(_key);
            var info = new ConnectionInfo(_host, _port, _user,
                new PrivateKeyAuthenticationMethod(_user, key))
            { Timeout = TimeSpan.FromSeconds(15) };

            var client = new SshClient(info);
            client.HostKeyReceived += (_, e) => e.CanTrust = true;
            client.Connect();

            var shell = client.CreateShellStream("xterm-256color", 80, 24, 800, 600, 4096);
            var pty = new SshPtyConnection(client, shell);

            // The seam the whole spike is about.
            term.AttachConnection(pty);
            Check("AttachConnection accepts an SSH-backed IPtyConnection", true);

            // The broadcast hook: in Swift this needed subclassing
            // LocalProcessTerminalView to override send(source:data:). Here it is
            // an event.
            var sawInput = false;
            term.InputSent += (_, _) => sawInput = true;

            await Task.Delay(1500);
            shell.WriteLine("echo SPIKE_$((6*7))_OK");

            var rendered = "";
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                rendered = await Dispatcher.UIThread.InvokeAsync(() => DumpBuffer(term));
                if (rendered.Contains("SPIKE_42_OK")) break;
            }

            Check("emulator rendered the remote output", rendered.Contains("SPIKE_42_OK"));

            // Scrollback read-back: this is what feeds the assistant its context.
            Check("buffer is readable as text", rendered.Trim().Length > 0,
                  $"{rendered.Split('\n').Count(l => l.Trim().Length > 0)} non-empty lines");

            // Resize through the control, all the way to the remote pty.
            await Dispatcher.UIThread.InvokeAsync(() => pty.Resize(120, 40));
            await Task.Delay(500);
            shell.WriteLine("tput cols");
            var resized = "";
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                resized = await Dispatcher.UIThread.InvokeAsync(() => DumpBuffer(term));
                if (resized.Contains("120")) break;
            }
            Check("resize propagates to the remote pty", resized.Contains("120"));

            Console.WriteLine("\n--- last rendered lines ---");
            foreach (var line in rendered.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6))
                Console.WriteLine("  | " + line.TrimEnd());

            _ = sawInput; // informational only; nothing types into it in headless mode
            pty.Kill();
        }
        catch (Exception ex)
        {
            Check("spike ran", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine($"\n{(failures == 0 ? "all checks passed" : $"{failures} check(s) failed")}");
        _report(failures == 0 ? 0 : 1);

        if (_headless) desktop.Shutdown();
    }

    // Reads the rendered screen out of the XTerm.NET engine. This is the
    // equivalent of TerminalRegistry.visibleText in the Swift app.
    private static string DumpBuffer(TerminalControl term)
    {
        var buffer = term.Terminal?.Buffer;
        if (buffer is null) return "";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < buffer.Lines.Length; i++)
        {
            var line = buffer.Lines[i];
            if (line is null) continue;
            sb.AppendLine(line.TranslateToString(true));
        }
        return sb.ToString();
    }
}
