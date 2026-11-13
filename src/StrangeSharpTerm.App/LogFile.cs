using System.Diagnostics;
using System.Text;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App;

/// <summary>
/// Where the app writes down what went wrong.
///
/// Everything that fails quietly here already says so through
/// <see cref="Trace"/> -- a pane that would not open, a preference that would
/// not save, a binding Avalonia could not resolve -- and a windowed app sends
/// Trace nowhere. This is the somewhere: a listener, so no call site changes
/// and nothing has to remember to use it.
///
/// It sits in <c>logs/</c> beside the inventory, so the variable that points a
/// run at a throwaway inventory points its log somewhere throwaway too. It is
/// one file and the one before it, each capped, because a log nobody reads
/// must not be the thing that fills a disk.
///
/// Every line goes through <see cref="Redaction"/> first. An exception's
/// message is whatever a server or a library chose to put in it, and a log is
/// the file people attach to a bug report.
/// </summary>
public sealed class LogFile : TraceListener
{
    public const string FileName = "strangesharpterm.log";

    /// <summary>About a megabyte, twice over: this file and the one before it.</summary>
    public const long DefaultLimit = 1024 * 1024;

    private readonly string _path;
    private readonly long _limit;
    private readonly Func<DateTimeOffset> _now;
    private readonly StringBuilder _partial = new();
    private readonly Lock _gate = new();

    public LogFile(string path, long limit = DefaultLimit, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _limit = limit;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string FilePath => _path;

    /// <summary>The file before this one, kept so a rotation does not take the crash with it.</summary>
    public string PreviousPath => _path + ".1";

    public static string DefaultPath() =>
        Path.Combine(Path.GetDirectoryName(Preferences.DefaultPath()) ?? ".", "logs", FileName);

    public override void Write(string? message)
    {
        lock (_gate)
            _partial.Append(message);
    }

    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            _partial.Append(message);
            var text = _partial.ToString();
            _partial.Clear();
            if (!IsNoise(text))
                Append(text);
        }
    }

    /// <summary>
    /// A binding whose path runs through something that is null <em>yet</em>:
    /// the detail pane before a host is selected, the palette before it is
    /// opened. Avalonia reports each one on every launch, two dozen at a time,
    /// and none is a fault -- left in, they are what a real failure would be
    /// buried under. A binding to a property that does not exist says
    /// something else and is kept.
    /// </summary>
    internal static bool IsNoise(string text) =>
        text.StartsWith("[Binding]", StringComparison.Ordinal)
        && text.Contains("'Value is null.'", StringComparison.Ordinal);

    /// <summary>
    /// Never throws. A log that cannot be written is not a reason to fail the
    /// thing that was being logged, which has usually failed already.
    /// </summary>
    private void Append(string text)
    {
        try
        {
            var line = $"{_now():yyyy-MM-ddTHH:mm:ss.fffZ} {Redaction.Scrub(text).Text}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetBytes(line);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var file = new FileInfo(_path);
            if (file.Exists && file.Length + bytes.Length > _limit)
                File.Move(_path, PreviousPath, overwrite: true);

            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Starts logging, and hears the two failures nothing else would report.
    ///
    /// An unhandled exception still ends the app: carrying on after one is a
    /// decision about every piece of state it interrupted, and this is not the
    /// place to make it. What changes is that it is written down first. A task
    /// nobody awaited fails in silence by design, so that is written down too.
    /// </summary>
    public static LogFile Install(string? path = null)
    {
        var log = new LogFile(path ?? DefaultPath());
        Trace.Listeners.Add(log);
        Trace.AutoFlush = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.WriteLine($"unhandled exception, the app is stopping: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            log.WriteLine($"a background task failed and nothing was waiting for it: {e.Exception}");

        var version = typeof(LogFile).Assembly.GetName().Version;
        log.WriteLine($"started {version} on {Environment.OSVersion}");
        return log;
    }

    /// <summary>
    /// Shows the log in Finder or Explorer -- the file selected where there is
    /// one, its folder where nothing has been written yet.
    /// </summary>
    public static void Reveal(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(folder);
            var exists = File.Exists(path);

            var start = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { ArgumentList = { exists ? "-R" : folder } }
                : OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("explorer.exe", exists ? $"/select,\"{path}\"" : $"\"{folder}\"")
                    : new ProcessStartInfo("xdg-open") { ArgumentList = { folder } };
            if (OperatingSystem.IsMacOS() && exists)
                start.ArgumentList.Add(path);

            using var _ = Process.Start(start);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"showing {path} failed: {e.Message}");
        }
    }
}
