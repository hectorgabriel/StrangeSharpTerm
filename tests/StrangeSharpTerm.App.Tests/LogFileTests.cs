using System.Diagnostics;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The log: what a windowed app does with the failures it used to send nowhere.
///
/// It is a file people attach to a bug report, which is what most of this is
/// about -- it must not hold a secret, must not grow without limit, and must
/// never be the reason something else fails.
/// </summary>
public sealed class LogFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"sst-log-{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_folder, "logs", LogFile.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static readonly DateTimeOffset Noon = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ALineIsWrittenWithWhenItHappened()
    {
        var log = new LogFile(LogPath, now: () => Noon);

        log.WriteLine("opening web1 failed: connection refused");

        File.ReadAllText(LogPath).ShouldBe(
            $"2026-11-13T12:00:00.000Z opening web1 failed: connection refused{Environment.NewLine}");
    }

    [Fact]
    public void WhatTraceIsToldReachesTheFile()
    {
        var log = new LogFile(LogPath);
        Trace.Listeners.Add(log);
        try
        {
            Trace.WriteLine("saving preferences.json failed: disk full");
        }
        finally
        {
            Trace.Listeners.Remove(log);
        }

        File.ReadAllText(LogPath).ShouldContain("saving preferences.json failed: disk full");
    }

    [Fact]
    public void PiecesWrittenSeparatelyMakeOneLine()
    {
        var log = new LogFile(LogPath, now: () => Noon);

        log.Write("binding ");
        log.Write("failed: ");
        log.WriteLine("no such property");

        File.ReadAllLines(LogPath).ShouldBe(["2026-11-13T12:00:00.000Z binding failed: no such property"]);
    }

    [Fact]
    public void ABindingThatIsOnlyWaitingForItsValueIsNotAFailure()
    {
        var log = new LogFile(LogPath);

        log.WriteLine("[Binding]An error occurred binding 'Text' to 'Detail.Name' at 'Detail': 'Value is null.' (TextBlock #1)");
        log.WriteLine("[Binding]An error occurred binding 'Text' to 'Nmae' at 'Nmae': 'Could not find a matching property accessor.' (TextBlock #2)");

        File.ReadAllLines(LogPath).ShouldHaveSingleItem().ShouldContain("Nmae");
    }

    [Fact]
    public void ASecretInAMessageIsNotWrittenDown()
    {
        var log = new LogFile(LogPath);

        log.WriteLine("request failed: 401 for key sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789");

        var written = File.ReadAllText(LogPath);
        written.ShouldNotContain("abcdefghijklmnopqrstuvwxyz");
        written.ShouldContain("request failed");
    }

    [Fact]
    public void AFullFileIsKeptAsThePreviousOneAndANewOneStarted()
    {
        var log = new LogFile(LogPath, limit: 200);

        log.WriteLine("first " + new string('a', 150));
        log.WriteLine("second " + new string('b', 150));

        File.ReadAllText(log.PreviousPath).ShouldContain("first");
        var current = File.ReadAllText(LogPath);
        current.ShouldContain("second");
        current.ShouldNotContain("first");
    }

    [Fact]
    public void OnlyOnePreviousFileIsKept()
    {
        var log = new LogFile(LogPath, limit: 200);

        log.WriteLine("first " + new string('a', 150));
        log.WriteLine("second " + new string('b', 150));
        log.WriteLine("third " + new string('c', 150));

        Directory.GetFiles(Path.GetDirectoryName(LogPath)!).Length.ShouldBe(2);
        File.ReadAllText(log.PreviousPath).ShouldContain("second");
        File.ReadAllText(LogPath).ShouldContain("third");
    }

    [Fact]
    public void ALogThatCannotBeWrittenFailsNothing()
    {
        // A file where the folder should be: nothing can be created under it.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "logs"), "in the way");
        var log = new LogFile(LogPath);

        Should.NotThrow(() => log.WriteLine("this goes nowhere"));
    }

    [Fact]
    public void TheLogSitsBesideTheInventory()
    {
        var folder = Path.GetDirectoryName(Preferences.DefaultPath())!;

        LogFile.DefaultPath().ShouldBe(Path.Combine(folder, "logs", LogFile.FileName));
    }

    [Fact]
    public void TheSheetShowsTheLogWhereThereIsOne()
    {
        var shown = 0;
        var sheet = new SettingsViewModel(
            new AppTheme(), () => Task.CompletedTask, () => Task.CompletedTask, revealLog: () => shown++);

        sheet.HasLog.ShouldBeTrue();
        sheet.RevealLogCommand.Execute(null);

        shown.ShouldBe(1);
    }

    [Fact]
    public void ASheetWithNoLogOffersNone()
    {
        var sheet = new SettingsViewModel(new AppTheme(), () => Task.CompletedTask, () => Task.CompletedTask);

        sheet.HasLog.ShouldBeFalse();
        Should.NotThrow(() => sheet.RevealLogCommand.Execute(null));
    }
}
