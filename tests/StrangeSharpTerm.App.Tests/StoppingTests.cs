using StrangeSharpTerm.App.Assistant;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// A command that runs out of time is stopped on the server, and a person
/// pressing Stop stops the command as well as the waiting.
/// </summary>
public class StoppingTests
{
    private sealed class NoHealth : IServerHealth
    {
        public ServerMetrics Collect() => new();
    }

    private static SshHostAccess Access(RecordingCommands commands, string? password = null) =>
        new("web-01", commands, new NoHealth(), () => null, () => password);

    /// <summary>
    /// What it said before it was stopped comes back with it, from both
    /// streams, with the signal the server says ended it. An empty "partial
    /// output" is what this used to send.
    /// </summary>
    [Fact]
    public async Task AStoppedCommandKeepsWhatItSaid()
    {
        var commands = new RecordingCommands
        {
            Answer = new CommandResult(-1, "/var/log 12G", "du: cannot read /proc/1") { Stopped = true, ExitSignal = "TERM" },
        };

        var outcome = await Access(commands).Run("du -xh /", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        outcome.TimedOut.ShouldBeTrue();
        outcome.StoppedBy.ShouldBe("TERM");
        outcome.MayStillBeRunning.ShouldBeFalse();
        outcome.Output.ShouldBe("/var/log 12G\ndu: cannot read /proc/1");
    }

    /// <summary>No signal back is not taken to mean it stopped.</summary>
    [Fact]
    public async Task AServerThatNeverSaidItStoppedLeavesItPossiblyRunning()
    {
        var commands = new RecordingCommands { Answer = new CommandResult(-1, "", "") { Stopped = true } };

        var outcome = await Access(commands).Run("du -xh /", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        outcome.MayStillBeRunning.ShouldBeTrue();
    }

    /// <summary>
    /// The caller's cancellation reaches the command itself, on the plain path
    /// and on the one that feeds sudo its password. It reached only the task
    /// wrapped around them, so Stop left a ten-minute plan command running.
    /// </summary>
    [Fact]
    public async Task StopReachesTheCommand()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var plain = new RecordingCommands();
        await Access(plain).Run("uptime", TimeSpan.FromSeconds(1), stopping.Token);
        plain.Tokens.ShouldBe([stopping.Token]);

        var fed = new RecordingCommands();
        await Access(fed, password: "hunter2").Run("sudo apt-get update", TimeSpan.FromSeconds(1), stopping.Token);
        fed.Tokens.ShouldAllBe(token => token == stopping.Token);
        fed.Ran.ShouldContain(ran => ran.Input != null);
    }
}
