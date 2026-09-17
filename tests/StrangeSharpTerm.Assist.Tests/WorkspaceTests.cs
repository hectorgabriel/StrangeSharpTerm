using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class FilePolicyTests
{
    private const string Root = "/home/ops/srv/app";

    [Fact]
    public void ReadingInsideTheFolderNeedsNobody()
    {
        FilePolicy.Judge(FileOperation.Read, Root, "conf/nginx.conf").MayRunUnattended.ShouldBeTrue();
        FilePolicy.Judge(FileOperation.List, Root, ".").MayRunUnattended.ShouldBeTrue();
    }

    [Fact]
    public void EveryWriteStops()
    {
        var judgement = FilePolicy.Judge(FileOperation.Write, Root, "conf/nginx.conf");

        judgement.MayRunUnattended.ShouldBeFalse();
        judgement.IsRefused.ShouldBeFalse();
        judgement.Reason.ShouldContain("conf/nginx.conf");
    }

    [Theory]
    [InlineData("../../../etc/shadow")]
    [InlineData("/etc/shadow")]
    [InlineData("conf/../../../root/.ssh/id_rsa")]
    public void OutsideTheFolderIsRefusedRatherThanAsked(string path)
    {
        // Refused, not asked. The person answered this when they chose the
        // folder, and a bar that asks anyway is a bar people stop reading.
        var judgement = FilePolicy.Judge(FileOperation.Read, Root, path);

        judgement.IsRefused.ShouldBeTrue();
        judgement.MayRunUnattended.ShouldBeFalse();
        judgement.Reason.ShouldContain("outside this workspace");
    }

    [Fact]
    public void AFileThatDecidesWhoCanGetInIsMarkedEvenInsideTheFolder()
    {
        // Opening your home directory says the assistant may edit your project.
        // It does not say it may append a key to authorized_keys, so that write
        // arrives in red.
        var judgement = FilePolicy.Judge(FileOperation.Write, "/home/ops", ".ssh/authorized_keys");

        judgement.IsDestructive.ShouldBeTrue();
        judgement.IsRefused.ShouldBeFalse();
    }

    [Fact]
    public void ADeleteIsAlwaysLoud() =>
        FilePolicy.Judge(FileOperation.Delete, Root, "app.py").IsDestructive.ShouldBeTrue();

    [Fact]
    public void ACallWithNoPathIsRefusedRatherThanGuessedAt() =>
        FilePolicy.Judge(FileOperation.Read, Root, "").IsRefused.ShouldBeTrue();
}

public class DiffTests
{
    [Fact]
    public void ItShowsTheLinesThatChangeAndTheOnesAround()
    {
        var before = "server {\n  listen 80;\n  root /srv;\n}\n";
        var after = "server {\n  listen 8080;\n  root /srv;\n}\n";

        var diff = Diff.Between(before, after);

        diff.Added.ShouldBe(1);
        diff.Removed.ShouldBe(1);
        diff.Text.ShouldContain("-   listen 80;");
        diff.Text.ShouldContain("+   listen 8080;");
        // The unchanged line above it is context, so the change is readable
        // without opening the file.
        diff.Text.ShouldContain("  server {");
        diff.Summary.ShouldBe("1 line added, 1 removed");
    }

    [Fact]
    public void TheSameTextIsNoChangeAtAll()
    {
        var diff = Diff.Between("one\ntwo\n", "one\ntwo\n");

        diff.IsEmpty.ShouldBeTrue();
        diff.Summary.ShouldBe("nothing changes");
    }

    [Fact]
    public void LineEndingsAloneAreNotAChange() =>
        // A Windows text box hands back CRLF for a file that arrived with LF.
        // If that read as four hundred changed lines, the diff would be useless
        // exactly where it matters.
        Diff.Between("one\ntwo\n", "one\r\ntwo\r\n").IsEmpty.ShouldBeTrue();

    [Fact]
    public void ARewriteTooLargeToReadIsCountedInstead()
    {
        var before = string.Join('\n', Enumerable.Range(0, 900).Select(line => $"line {line}"));
        var after = string.Join('\n', Enumerable.Range(0, 900).Select(line => $"other {line}"));

        var diff = Diff.Between(before, after);

        diff.Summarised.ShouldBeTrue();
        diff.Text.ShouldContain("900 lines");
    }
}

/// <summary>
/// The agent with a folder open: what it may read, what stops, and what a
/// refusal tells the model.
/// </summary>
public class WorkspaceAgentTests
{
    private static HostAgent Agent(
        FakeWorkspace workspace,
        ICommandGate gate,
        params IReadOnlyList<AssistEvent>[] turns) =>
        new(new ScriptedBackend(turns), new FakeHost("web-01"), Fixtures.Settings, gate, null, workspace);

    private static AskOptions Editing { get; } = new() { MayEditFiles = true };

    [Fact]
    public async Task WithNoFolderOpenThereAreNoFileToolsAtAll()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("I would need to see the file."));
        var agent = new HostAgent(
            backend,
            new FakeHost("web-01"),
            Fixtures.Settings,
            new StandingAnswer(true),
            null,
            new FakeWorkspace(root: ""));

        await agent.Ask("what is in nginx.conf?", cancellationToken: TestContext.Current.CancellationToken);

        backend.Requests.Single().Tools.ShouldBeEmpty();
        backend.Requests.Single().System.ShouldNotContain("workspace");
    }

    [Fact]
    public async Task OpeningAFolderOffersReadingAndNotWriting()
    {
        var workspace = new FakeWorkspace().With("app.py", "print()");
        var backend = new ScriptedBackend(ScriptedBackend.Says("Nothing to change."));
        var agent = new HostAgent(
            backend, new FakeHost("web-01"), Fixtures.Settings, new StandingAnswer(true), null, workspace);

        await agent.Ask("what is here?", cancellationToken: TestContext.Current.CancellationToken);

        var offered = backend.Requests.Single().Tools.Select(tool => tool.Name).ToArray();
        offered.ShouldBe([WorkspaceTools.ListFiles, WorkspaceTools.ReadFile]);
        // Reading needs no switch -- opening the folder was the decision -- and
        // the model is told plainly that it cannot write.
        backend.Requests.Single().System.ShouldContain("editing is turned off");
    }

    [Fact]
    public async Task ReadingAFileRunsWithoutAsking()
    {
        var workspace = new FakeWorkspace().With("conf/nginx.conf", "listen 80;");
        var gate = new RecordingGate();
        var agent = Agent(
            workspace,
            gate,
            ScriptedBackend.Calls(WorkspaceTools.ReadFile, new { path = "conf/nginx.conf" }),
            ScriptedBackend.Says("It listens on 80."));

        await agent.Ask("which port?", cancellationToken: TestContext.Current.CancellationToken);

        gate.Asked.ShouldBeEmpty();
        var step = agent.Entries.OfType<TranscriptEntry.Step>().Single();
        step.State.ShouldBe(StepState.Ran);
        step.IsFile.ShouldBeTrue();
        step.Command.ShouldBe("read conf/nginx.conf");
        step.Output.ShouldBe("listen 80;");
    }

    [Fact]
    public async Task AWriteStopsAndTheQuestionCarriesTheLinesThatChange()
    {
        var workspace = new FakeWorkspace().With("conf/nginx.conf", "server {\n  listen 80;\n}\n");
        var gate = new RecordingGate(answer: true);
        var agent = Agent(
            workspace,
            gate,
            ScriptedBackend.Calls(
                WorkspaceTools.WriteFile,
                new { path = "conf/nginx.conf", content = "server {\n  listen 8080;\n}\n", why = "move it off 80" }),
            ScriptedBackend.Says("Changed the port."));

        await agent.Ask("put it on 8080", Editing, TestContext.Current.CancellationToken);

        var asked = gate.Asked.Single();
        asked.Host.ShouldBe("web-01");
        asked.Command.ShouldBe("write conf/nginx.conf");
        asked.Why.ShouldBe("move it off 80");
        // The substance is in the bar itself, not one click away.
        asked.Detail.ShouldNotBeNull();
        asked.Detail.ShouldContain("+   listen 8080;");
        workspace.Written["conf/nginx.conf"].ShouldContain("8080");
    }

    [Fact]
    public async Task ARefusedWriteChangesNothingAndSaysSoPlainly()
    {
        var workspace = new FakeWorkspace().With("app.py", "print()");
        var gate = new RecordingGate(answer: false);
        var agent = Agent(
            workspace,
            gate,
            ScriptedBackend.Calls(
                WorkspaceTools.WriteFile,
                new { path = "app.py", content = "print('hello')", why = "add a greeting" }),
            ScriptedBackend.Says("Left it alone."));

        await agent.Ask("add a greeting", Editing, TestContext.Current.CancellationToken);

        workspace.Written.ShouldBeEmpty();
        agent.Entries.OfType<TranscriptEntry.Step>().Single().State.ShouldBe(StepState.Refused);
    }

    [Fact]
    public async Task APathOutsideTheFolderNeverReachesAPerson()
    {
        var workspace = new FakeWorkspace();
        var gate = new RecordingGate();
        var agent = Agent(
            workspace,
            gate,
            ScriptedBackend.Calls(WorkspaceTools.ReadFile, new { path = "../../../etc/shadow" }),
            ScriptedBackend.Says("I cannot read that."));

        await agent.Ask("what is in /etc/shadow?", Editing, TestContext.Current.CancellationToken);

        gate.Asked.ShouldBeEmpty();
        agent.Entries.OfType<TranscriptEntry.Step>().Single().State.ShouldBe(StepState.Refused);
    }

    [Fact]
    public async Task AWriteThatWouldPutTheRedactionMarkerBackIsStopped()
    {
        // The trap this closes: the model reads a scrubbed .env, edits one line,
        // and sends the whole file back -- with [redacted] where the password
        // was. Nobody is asked, because there is no version of this that is
        // right.
        var workspace = new FakeWorkspace().With(".env", "DB_PASSWORD=hunter2\nDEBUG=0\n");
        var gate = new RecordingGate();
        var agent = Agent(
            workspace,
            gate,
            ScriptedBackend.Calls(
                WorkspaceTools.WriteFile,
                new { path = ".env", content = "DB_PASSWORD=[redacted]\nDEBUG=1\n", why = "turn debugging on" }),
            ScriptedBackend.Says("I cannot write that back."));

        await agent.Ask("turn debugging on", Editing, TestContext.Current.CancellationToken);

        gate.Asked.ShouldBeEmpty();
        workspace.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task AndNotIntoANewFileEither()
    {
        // Not the same failure and just as bad: a .env.production copied from a
        // scrubbed .env is a deploy whose password is the word that hid the
        // password.
        var workspace = new FakeWorkspace().With(".env", "DB_PASSWORD=hunter2\n");
        var gate = new RecordingGate();
        var agent = Agent(
            workspace,
            gate,
            ScriptedBackend.Calls(
                WorkspaceTools.WriteFile,
                new { path = ".env.production", content = "DB_PASSWORD=[redacted]\n", why = "copy it for production" }),
            ScriptedBackend.Says("I cannot copy that."));

        await agent.Ask("make a production copy", Editing, TestContext.Current.CancellationToken);

        gate.Asked.ShouldBeEmpty();
        workspace.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFileThatAlreadySaysExactlyThatIsNotAQuestionForAnybody()
    {
        var workspace = new FakeWorkspace().With("app.py", "print()");
        var gate = new RecordingGate();
        var agent = Agent(
            workspace,
            gate,
            ScriptedBackend.Calls(
                WorkspaceTools.WriteFile,
                new { path = "app.py", content = "print()", why = "make sure" }),
            ScriptedBackend.Says("It was already right."));

        await agent.Ask("make sure it prints", Editing, TestContext.Current.CancellationToken);

        gate.Asked.ShouldBeEmpty();
        workspace.Written.ShouldBeEmpty();
        agent.Entries.OfType<TranscriptEntry.Step>().Single().Output.ShouldBe("It already says exactly that.");
    }

    [Fact]
    public async Task AFileIsScrubbedBeforeTheModelSeesIt()
    {
        var workspace = new FakeWorkspace().With(".env", "DB_PASSWORD=hunter2\n");
        var backend = new ScriptedBackend(
            ScriptedBackend.Calls(WorkspaceTools.ReadFile, new { path = ".env" }),
            ScriptedBackend.Says("The password is set."));
        var agent = new HostAgent(
            backend, new FakeHost("web-01"), Fixtures.Settings, new StandingAnswer(true), null, workspace);

        await agent.Ask("is the password set?", cancellationToken: TestContext.Current.CancellationToken);

        var sent = backend.Requests[1].Messages
            .SelectMany(message => message.ToolResults)
            .Single().Output;
        sent.ShouldNotContain("hunter2");
        sent.ShouldContain(Redaction.Marker);
        // And told why, so it does not write the marker back into the file.
        sent.ShouldContain("secret(s) were removed");
    }

    [Fact]
    public async Task ReadingSpendsTheSameBudgetACommandDoes()
    {
        var workspace = new FakeWorkspace().With("app.py", "print()");
        var budget = new CommandBudget(1);
        var agent = Agent(
            workspace,
            new StandingAnswer(true),
            ScriptedBackend.Calls(WorkspaceTools.ReadFile, new { path = "app.py" }, "one"),
            ScriptedBackend.Calls(WorkspaceTools.ReadFile, new { path = "app.py" }, "two"),
            ScriptedBackend.Says("Enough."));

        await agent.Ask(
            "read it twice",
            new AskOptions { Budget = budget },
            TestContext.Current.CancellationToken);

        var steps = agent.Entries.OfType<TranscriptEntry.Step>().ToArray();
        steps[0].State.ShouldBe(StepState.Ran);
        steps[1].State.ShouldBe(StepState.Skipped);
    }
}
