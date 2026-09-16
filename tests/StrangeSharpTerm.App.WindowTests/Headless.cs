using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// A window with no screen behind it.
///
/// The plan asks for a headless UI driver at M4–M5, and splits are the argument
/// for it: rebuilding the pane layout detached every terminal and re-attached
/// it, which tears its connection down, and nothing in a view-model test could
/// see that. What it needs is a real visual tree — templates applied, layout
/// run, input delivered — without a display.
///
/// Avalonia.Headless provides the platform. This is the harness around it:
/// one application for the whole assembly, and a way to run a test body on its
/// UI thread. <c>Avalonia.Headless.XUnit</c> would provide an
/// <c>[AvaloniaFact]</c> attribute instead, at the cost of pinning
/// <c>xunit.v3.extensibility.core</c> against a version this repo does not
/// choose; the attribute is not worth the coupling when the harness is this
/// small.
/// </summary>
public static class Headless
{
    private static readonly Lock Gate = new();
    private static HeadlessUnitTestSession? _session;

    /// <summary>
    /// Runs a test body on the UI thread, with an application around it.
    ///
    /// Every test shares one session because Avalonia can be initialised once
    /// per process; they are serialised rather than parallel for the same
    /// reason, which is what <see cref="CollectionAttribute"/> on the tests
    /// arranges.
    /// </summary>
    public static void Run(Action body) => Session().Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Waits for work the test started, pumping the dispatcher while it waits.
    ///
    /// A test body runs <em>on</em> the UI thread, so awaiting inside it waits
    /// for a continuation that only that thread can run: the wait and the
    /// runner are the same thread, and the run never finishes. Pumping is what
    /// breaks that, and the deadline is what turns a mistake into a failed test
    /// rather than a test suite that hangs.
    /// </summary>
    public static void Finish(Task task, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"the work did not finish within {seconds}s");
            Thread.Sleep(1);
        }

        // Rethrows whatever it failed with, rather than swallowing it.
        task.GetAwaiter().GetResult();
    }

    private static HeadlessUnitTestSession Session()
    {
        lock (Gate)
        {
            if (_session is not null)
                return _session;

            // Before the application exists: it reads the inventory path on
            // startup, and a test must never open — let alone save — the real
            // one. The same variable the window is developed against.
            var scratch = Path.Combine(Path.GetTempPath(), $"strangesharpterm-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            Environment.SetEnvironmentVariable(InventoryLoader.InventoryVariable, Path.Combine(scratch, "inventory.json"));
            Environment.SetEnvironmentVariable(InventoryLoader.SshConfigVariable, Path.Combine(scratch, "config"));

            return _session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        }
    }
}

/// <summary>
/// The real application, on the headless platform.
///
/// <see cref="App"/> itself, not a stand-in: the point of these tests is that
/// the styles, the control themes and the theme tokens are the ones that ship.
/// It builds no window here, because a headless session has no desktop
/// lifetime for <c>OnFrameworkInitializationCompleted</c> to find.
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>
/// Where a pane is drawn: the outlined frame the layout keeps for it, whatever
/// it puts between that frame and the pane's own view.
///
/// Asked for by walking up rather than by taking the view's immediate parent,
/// because a tiled frame holds a name strip above its pane and the pane is then
/// a grandchild. What these tests are about is the frame — that it is the same
/// one after a move, that it is hidden rather than removed — and that is true
/// however many layers deep the view sits.
/// </summary>
public static class Frames
{
    public static Border Of(Control view)
    {
        for (var parent = view.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is Border frame && frame.Classes.Contains("pane"))
                return frame;
        }

        throw new InvalidOperationException("this view is not in a pane frame");
    }
}

/// <summary>
/// One at a time: there is a single Avalonia application in the process, and a
/// window test that ran beside another would share its dispatcher.
/// </summary>
[CollectionDefinition("window")]
public sealed class WindowCollection;
