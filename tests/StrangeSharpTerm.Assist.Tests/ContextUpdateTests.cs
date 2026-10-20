using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// A question carries what changed about the host, not everything about it
/// again.
///
/// Every question used to resend the whole block: the same kernel, the same
/// hundred and twenty lines of terminal with a few new ones at the bottom, all
/// of it already in the conversation above.
/// </summary>
public class ContextUpdateTests
{
    private static readonly ServerMetrics Steady = new()
    {
        Uptime = "10 days",
        LoadAverages = [0.50, 0.40, 0.30],
        MemoryUsedBytes = 2_147_483_648,
        MemoryTotalBytes = 8_589_934_592,
    };

    private static string Screen(int from, int to, string prompt = "deploy@web-01:~$ ") =>
        string.Join('\n', [.. Enumerable.Range(from, to - from).Select(line => $"line {line}"), prompt]);

    private static (HostAgent Agent, FakeHost Host, ScriptedBackend Backend) Talking(int answers)
    {
        var backend = new ScriptedBackend([.. Enumerable.Range(0, answers).Select(index => ScriptedBackend.Says($"Answer {index}."))]);
        var host = new FakeHost("web-01")
        {
            Snapshot = new HostSnapshot { Kernel = "Linux 6.1.0 x86_64", Metrics = Steady, TerminalTail = Screen(0, 10) },
        };
        return (new HostAgent(backend, host, Fixtures.Settings, new RecordingGate()), host, backend);
    }

    private static string Asked(ScriptedBackend backend, int request) =>
        backend.Requests[request].Messages[^1].Text.ShouldNotBeNull();

    [Fact]
    public async Task TheFirstQuestionCarriesEverything()
    {
        var (agent, _, backend) = Talking(1);

        await agent.Ask("how is it?", null, TestContext.Current.CancellationToken);

        var first = Asked(backend, 0);
        first.ShouldContain("System: Linux 6.1.0 x86_64");
        first.ShouldContain("- load average 0.50, 0.40, 0.30");
        first.ShouldContain("line 0");
    }

    /// <summary>Only what moved, said as such, so the model knows the rest stands.</summary>
    [Fact]
    public async Task ALaterQuestionCarriesOnlyWhatChanged()
    {
        var (agent, host, backend) = Talking(2);
        await agent.Ask("how is it?", null, TestContext.Current.CancellationToken);

        host.Snapshot = host.Snapshot with
        {
            Metrics = Steady with { LoadAverages = [3.10, 1.20, 0.60] },
            // Two commands' worth printed, so the window scrolled by four.
            TerminalTail = Screen(4, 10, "deploy@web-01:~$ df -h") + "\n/dev/sda1 98% /\ndeploy@web-01:~$ ",
        };
        await agent.Ask("and now?", null, TestContext.Current.CancellationToken);

        var second = Asked(backend, 1);
        second.ShouldNotContain("System:");
        second.ShouldContain("Changed since the last question:\n- load average 3.10, 1.20, 0.60");
        second.ShouldNotContain("memory");
        second.ShouldContain("New in the terminal since the last question");
        second.ShouldContain("```\ndeploy@web-01:~$ df -h\n/dev/sda1 98% /\ndeploy@web-01:~$ \n```");
        second.ShouldNotContain("line 5");
        second.ShouldEndWith("and now?");
    }

    /// <summary>An empty block would look like a probe that failed.</summary>
    [Fact]
    public async Task NothingChangedIsSaid()
    {
        var (agent, _, backend) = Talking(2);
        await agent.Ask("how is it?", null, TestContext.Current.CancellationToken);
        await agent.Ask("and now?", null, TestContext.Current.CancellationToken);

        Asked(backend, 1).ShouldBe("Host: web-01\nNothing about the host has changed since the last question.\n\nand now?");
    }

    /// <summary>
    /// The disclosure shows what the next question carries, which is the
    /// update -- it is the request, not a description of it.
    /// </summary>
    [Fact]
    public async Task TheDisclosureShowsTheUpdate()
    {
        var (agent, host, backend) = Talking(2);
        await agent.Ask("how is it?", null, TestContext.Current.CancellationToken);
        host.Snapshot = host.Snapshot with { Kernel = "Linux 6.2.0 x86_64" };

        var preview = (await agent.Context(TestContext.Current.CancellationToken)).Render();
        await agent.Ask("and now?", null, TestContext.Current.CancellationToken);

        preview.ShouldBe("Host: web-01\nSystem: Linux 6.2.0 x86_64");
        Asked(backend, 1).ShouldStartWith(preview);
    }

    /// <summary>A retry goes back to what the conversation knew before the question it takes back.</summary>
    [Fact]
    public async Task ARetryIsMeasuredAgainstWhatCameBefore()
    {
        var (agent, _, backend) = Talking(3);
        await agent.Ask("how is it?", null, TestContext.Current.CancellationToken);
        agent.Rewind();
        await agent.Ask("how is it, again?", null, TestContext.Current.CancellationToken);

        Asked(backend, 1).ShouldContain("System: Linux 6.1.0 x86_64");
    }

    [Fact]
    public async Task AClearStartsFromNothing()
    {
        var (agent, _, backend) = Talking(2);
        await agent.Ask("how is it?", null, TestContext.Current.CancellationToken);
        agent.Clear();
        await agent.Ask("how is it?", null, TestContext.Current.CancellationToken);

        Asked(backend, 1).ShouldContain("line 0");
    }

    /// <summary>
    /// The exchange that held the full account can be compacted away, and with
    /// it everything the update left out -- so the question that compacts says
    /// everything again.
    /// </summary>
    [Fact]
    public async Task CompactingSendsEverythingAgain()
    {
        var turns = new List<IReadOnlyList<AssistEvent>>();
        for (var index = 0; index < 20; index++)
        {
            turns.Add(ScriptedBackend.Runs("cat /var/log/syslog", "reading the log", $"call_{index}"));
            turns.Add(ScriptedBackend.Says($"Answer {index}."));
        }
        var backend = new ScriptedBackend([.. turns]);
        var host = new FakeHost("web-01")
        {
            Snapshot = new HostSnapshot { Kernel = "Linux 6.1.0 x86_64", Metrics = Steady },
            Answer = _ => new CommandOutcome(0, new string('x', AssistLimits.MaxOutputCharacters)),
        };
        var agent = new HostAgent(backend, host, Fixtures.Settings, new RecordingGate());

        for (var index = 0; index < 20; index++)
            await agent.Ask($"question {index}?", new AskOptions { MayRunCommands = true }, TestContext.Current.CancellationToken);

        // By the last question the first is long gone, and the kernel was only
        // ever said in it -- so it is said again.
        var sent = backend.Requests[^1].Messages;
        sent.ShouldContain(message => message.Text != null
            && message.Text.Contains("question 19?")
            && message.Text.Contains("System: Linux 6.1.0 x86_64"));
    }

    [Fact]
    public void TheTerminalIsReadForWhatIsNew()
    {
        // Scrolled: only what follows the old prompt, the prompt line included.
        var (scrolled, follows) = HostContext.Fresh(Screen(0, 10), Screen(3, 10, "$ ls") + "\na.txt\n$ ");
        scrolled.ShouldBe(["$ ls", "a.txt", "$ "]);
        follows.ShouldBeTrue();

        // Sitting at the same prompt: nothing.
        HostContext.Fresh(Screen(0, 10), Screen(0, 10)).Lines.ShouldBeEmpty();

        // Cleared, or a different shell: everything, and not called a continuation.
        var (cleared, continues) = HostContext.Fresh(Screen(0, 10), "fresh\n$ ");
        cleared.ShouldBe(["fresh", "$ "]);
        continues.ShouldBeFalse();
    }
}
