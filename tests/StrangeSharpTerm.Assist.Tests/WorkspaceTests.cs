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

/// <summary>
/// Every tool this app offers, checked for the one thing a compiler cannot see.
///
/// A schema is a JSON document inside a C# raw string literal, which means the
/// compiler is happy with a quote that JSON is not: the escaping can be lost in
/// an edit and the build stays green all the way to the provider, which then
/// rejects the request. That happened once, to a description containing a
/// quoted full stop.
/// </summary>
public class ToolSchemaTests
{
    public static TheoryData<string, string> Offered()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in WorkspaceTools.Reading
                     .Append(WorkspaceTools.Writer)
                     .Concat(WorkspaceTools.ReadingLocal)
                     .Append(WorkspaceTools.LocalWriter)
                     .Append(AssistTools.Runner)
                     .Append(AssistTools.FleetRunner(["web-01", "db-primary"])))
        {
            data.Add(tool.Name, tool.JsonSchema);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Offered))]
    public void EverySchemaIsJsonAndSaysWhatItRequires(string name, string schema)
    {
        var parsed = System.Text.Json.JsonDocument.Parse(schema).RootElement;

        name.ShouldNotBeEmpty();
        parsed.GetProperty("type").GetString().ShouldBe("object");
        parsed.TryGetProperty("properties", out var properties).ShouldBeTrue();

        // Everything named as required has to be a property that exists, or the
        // model is being asked for a field the schema never described.
        foreach (var required in parsed.GetProperty("required").EnumerateArray())
            properties.TryGetProperty(required.GetString()!, out _).ShouldBeTrue();
    }
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

/// <summary>
/// The agent with a folder open on each machine.
///
/// What these are about is the distinction: two filesystems with similar paths,
/// one of them the person's own computer. Every one of these would pass if the
/// two were confused for each other, except that each asserts which machine was
/// touched.
/// </summary>
public class LocalWorkspaceAgentTests
{
    private static AskOptions Editing { get; } = new() { MayEditFiles = true };

    private static HostAgent Agent(
        FakeWorkspace? host,
        FakeWorkspace? here,
        ICommandGate gate,
        params IReadOnlyList<AssistEvent>[] turns) =>
        new(new ScriptedBackend(turns), new FakeHost("web-01"), Fixtures.Settings, gate, null, host, here);

    [Fact]
    public async Task EachFolderBringsItsOwnToolsAndNeitherBringsTheOthers()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("Noted."));
        var agent = new HostAgent(
            backend,
            new FakeHost("web-01"),
            Fixtures.Settings,
            new StandingAnswer(true),
            null,
            null,
            new FakeWorkspace("/Users/you/project"));

        await agent.Ask("what is here?", Editing, TestContext.Current.CancellationToken);

        var offered = backend.Requests.Single().Tools.Select(tool => tool.Name).ToArray();
        offered.ShouldBe([
            WorkspaceTools.ListLocalFiles, WorkspaceTools.ReadLocalFile, WorkspaceTools.WriteLocalFile]);
        // Told which computer it is looking at, because "edit the config" is
        // ambiguous between two open folders in a way that matters.
        backend.Requests.Single().System.ShouldContain("own machine");
    }

    [Fact]
    public async Task WithBothOpenItIsOfferedSixAndToldWhichIsWhich()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("Noted."));
        var agent = new HostAgent(
            backend,
            new FakeHost("web-01"),
            Fixtures.Settings,
            new StandingAnswer(true),
            null,
            new FakeWorkspace(),
            new FakeWorkspace("/Users/you/project"));

        await agent.Ask("compare them", Editing, TestContext.Current.CancellationToken);

        backend.Requests.Single().Tools.Select(tool => tool.Name).ShouldBe([
            WorkspaceTools.ListFiles,
            WorkspaceTools.ReadFile,
            WorkspaceTools.WriteFile,
            WorkspaceTools.ListLocalFiles,
            WorkspaceTools.ReadLocalFile,
            WorkspaceTools.WriteLocalFile,
        ]);
    }

    [Fact]
    public async Task AWriteToThisMachineSaysSoInTheQuestionAndInTheRow()
    {
        var here = new FakeWorkspace("/Users/you/project").With("notes.md", "one\n");
        var gate = new RecordingGate(answer: true);
        var agent = Agent(
            null,
            here,
            gate,
            ScriptedBackend.Calls(
                WorkspaceTools.WriteLocalFile,
                new { path = "notes.md", content = "one\ntwo\n", why = "add the second line" }),
            ScriptedBackend.Says("Added it."));

        await agent.Ask("add a line to my notes", Editing, TestContext.Current.CancellationToken);

        // Whose machine, in the words the person is asked in -- not the host's
        // name on a card about a file that lands here.
        var asked = gate.Asked.Single();
        asked.Host.ShouldBe("this machine");
        asked.Reason.ShouldContain("this machine");

        var step = agent.Entries.OfType<TranscriptEntry.Step>().Single();
        step.Command.ShouldBe("write notes.md (this machine)");
        here.Written["notes.md"].ShouldBe("one\ntwo\n");
    }

    [Fact]
    public async Task AWriteToTheHostIsStillTheHosts()
    {
        var host = new FakeWorkspace().With("app.py", "print()");
        var gate = new RecordingGate(answer: true);
        var agent = Agent(
            host,
            new FakeWorkspace("/Users/you/project"),
            gate,
            ScriptedBackend.Calls(
                WorkspaceTools.WriteFile,
                new { path = "app.py", content = "print('hi')", why = "greet" }),
            ScriptedBackend.Says("Done."));

        await agent.Ask("make it greet", Editing, TestContext.Current.CancellationToken);

        gate.Asked.Single().Host.ShouldBe("web-01");
        agent.Entries.OfType<TranscriptEntry.Step>().Single().Command.ShouldBe("write app.py");
        host.Written.ShouldContainKey("app.py");
    }

    [Fact]
    public async Task APathOutsideTheLocalFolderIsRefusedThereToo()
    {
        var here = new FakeWorkspace("/Users/you/project");
        var gate = new RecordingGate();
        var agent = Agent(
            null,
            here,
            gate,
            ScriptedBackend.Calls(WorkspaceTools.ReadLocalFile, new { path = "../../.ssh/id_rsa" }),
            ScriptedBackend.Says("I cannot read that."));

        await agent.Ask("what is my private key?", Editing, TestContext.Current.CancellationToken);

        gate.Asked.ShouldBeEmpty();
        agent.Entries.OfType<TranscriptEntry.Step>().Single().State.ShouldBe(StepState.Refused);
        here.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task AskingForALocalFileWithNoLocalFolderOpenIsToldSoPlainly()
    {
        var gate = new RecordingGate();
        var agent = Agent(
            new FakeWorkspace(),
            null,
            gate,
            ScriptedBackend.Calls(WorkspaceTools.ReadLocalFile, new { path = "notes.md" }),
            ScriptedBackend.Says("There is no folder open here."));

        await agent.Ask("read my notes", Editing, TestContext.Current.CancellationToken);

        // The tool was not offered, so this is a model asking for something it
        // was not given -- answered rather than crashed on.
        agent.Entries.OfType<TranscriptEntry.Step>().ShouldBeEmpty();
    }

    [Fact]
    public async Task ALocalWriteNarratesNothingIntoTheHostsPane()
    {
        // The pane belongs to the server. A file on this machine has no terminal
        // to say so in, and saying it in the host's would be saying it about the
        // wrong computer.
        var here = new FakeWorkspace("/Users/you/project").With("notes.md", "one\n");
        var narrated = new List<AssistStep>();
        var agent = Agent(
            null,
            here,
            new StandingAnswer(true),
            ScriptedBackend.Calls(
                WorkspaceTools.WriteLocalFile,
                new { path = "notes.md", content = "two\n", why = "replace it" }),
            ScriptedBackend.Says("Done."));
        agent.Working += (_, step) => narrated.Add(step);

        await agent.Ask("replace my notes", Editing, TestContext.Current.CancellationToken);

        narrated.ShouldBeEmpty();
        here.Written.ShouldContainKey("notes.md");
    }
}

/// <summary>
/// The fan-out's folder, which is this machine's and not the hosts'.
///
/// A run is where one instruction becomes an action on eight servers. What it
/// gets is the other direction: read the runbook here, compare it with what the
/// machines actually have, write the findings down.
/// </summary>
public class FleetWorkspaceTests
{
    private static IReadOnlyList<FleetHost> Hosts(params string[] aliases) =>
        [.. aliases.Select(alias => new FleetHost(alias, () => new FakeHost(alias)))];

    [Fact]
    public async Task ItIsOfferedThisMachinesFilesAndNeverTheHosts()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("Noted."));
        var agent = new FleetAgent(backend, new StandingAnswer(true), null, new FakeWorkspace("/Users/you/runbooks"));

        await agent.Ask(
            "compare the configs",
            Hosts("web-01", "web-02"),
            mayRunCommands: true,
            mayEditFiles: true,
            TestContext.Current.CancellationToken);

        var offered = backend.Requests.Single().Tools.Select(tool => tool.Name).ToArray();
        offered.ShouldBe([
            AssistTools.RunCommand,
            WorkspaceTools.ListLocalFiles,
            WorkspaceTools.ReadLocalFile,
            WorkspaceTools.WriteLocalFile,
        ]);
        // Not read_file or write_file: a write fanned out across eight servers
        // from one approval is the thing this deliberately cannot do.
        offered.ShouldNotContain(WorkspaceTools.WriteFile);
    }

    [Fact]
    public async Task AskingForAHostsFilesIsTurnedDownWithSomewhereToGo()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Calls(WorkspaceTools.ReadFile, new { path = "conf/nginx.conf" }),
            ScriptedBackend.Says("I will ask about one host instead."));
        var agent = new FleetAgent(backend, new StandingAnswer(true), null, new FakeWorkspace("/Users/you/runbooks"));

        await agent.Ask(
            "read nginx.conf everywhere",
            Hosts("web-01"),
            mayRunCommands: true,
            mayEditFiles: true,
            TestContext.Current.CancellationToken);

        var told = backend.Requests[1].Messages.SelectMany(message => message.ToolResults).Single();
        told.Failed.ShouldBeTrue();
        told.Output.ShouldContain("Ask about one host on its own");
    }

    [Fact]
    public async Task WritingHereStopsAtTheSameGateWithTheSameDiff()
    {
        var here = new FakeWorkspace("/Users/you/runbooks").With("findings.md", "# Findings\n");
        var gate = new RecordingGate(answer: true);
        var backend = new ScriptedBackend(
            ScriptedBackend.Calls(
                WorkspaceTools.WriteLocalFile,
                new
                {
                    path = "findings.md",
                    content = "# Findings\n\n- web-01 is uncapped\n",
                    why = "write down what the run found",
                }),
            ScriptedBackend.Says("Written."));
        var agent = new FleetAgent(backend, gate, null, here);

        await agent.Ask(
            "check them and write it down",
            Hosts("web-01", "web-02"),
            mayRunCommands: true,
            mayEditFiles: true,
            TestContext.Current.CancellationToken);

        var asked = gate.Asked.Single();
        asked.Host.ShouldBe("this machine");
        asked.Detail.ShouldNotBeNull().ShouldContain("+ - web-01 is uncapped");
        here.Written["findings.md"].ShouldContain("uncapped");
    }

    [Fact]
    public async Task WithTheSwitchOffItCanReadHereAndNotWrite()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("Noted."));
        var agent = new FleetAgent(backend, new StandingAnswer(true), null, new FakeWorkspace("/Users/you/runbooks"));

        await agent.Ask(
            "what does the runbook say?",
            Hosts("web-01"),
            mayRunCommands: true,
            mayEditFiles: false,
            TestContext.Current.CancellationToken);

        backend.Requests.Single().Tools.Select(tool => tool.Name)
            .ShouldNotContain(WorkspaceTools.WriteLocalFile);
        backend.Requests.Single().System.ShouldContain("editing is turned off");
    }

    [Fact]
    public async Task WithNoFolderHereItIsOfferedNoFileToolsAtAll()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("Noted."));
        var agent = new FleetAgent(backend, new StandingAnswer(true));

        await agent.Ask(
            "look at them",
            Hosts("web-01"),
            mayRunCommands: true,
            mayEditFiles: true,
            TestContext.Current.CancellationToken);

        backend.Requests.Single().Tools.Select(tool => tool.Name).ShouldBe([AssistTools.RunCommand]);
    }
}

/// <summary>Forgetting a conversation, in both halves of it.</summary>
public class ClearingTests
{
    [Fact]
    public async Task ClearingEmptiesTheTranscriptAndWhatTheProviderWasTold()
    {
        var backend = new ScriptedBackend(
            ScriptedBackend.Says("It is 98% full."),
            ScriptedBackend.Says("Still 98%."));
        var agent = new HostAgent(backend, new FakeHost("web-01"), Fixtures.Settings, new StandingAnswer(true));

        await agent.Ask("how full?", cancellationToken: TestContext.Current.CancellationToken);
        agent.Entries.ShouldNotBeEmpty();

        var emptied = false;
        agent.Cleared += (_, _) => emptied = true;
        agent.Clear();

        emptied.ShouldBeTrue();
        agent.Entries.ShouldBeEmpty();
        agent.CanRewind.ShouldBeFalse();

        // And the next question arrives at a provider that has been told
        // nothing: one message, not three. A clear that emptied only the screen
        // would leave every later question carrying what nobody can see.
        await agent.Ask("and now?", cancellationToken: TestContext.Current.CancellationToken);
        backend.Requests[^1].Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheFleetForgetsTheSameWay()
    {
        var backend = new ScriptedBackend(ScriptedBackend.Says("All fine."), ScriptedBackend.Says("Still fine."));
        var agent = new FleetAgent(backend, new StandingAnswer(true));
        var hosts = new[] { new FleetHost("web-01", () => new FakeHost("web-01")) };

        await agent.Ask("how are they?", hosts, mayRunCommands: false,
            cancellationToken: TestContext.Current.CancellationToken);
        agent.Entries.ShouldNotBeEmpty();

        agent.Clear();

        agent.Entries.ShouldBeEmpty();
        await agent.Ask("and now?", hosts, mayRunCommands: false,
            cancellationToken: TestContext.Current.CancellationToken);
        backend.Requests[^1].Messages.Count.ShouldBe(1);
    }

    [Fact]
    public void SayingSomethingPutsItInTheTranscriptAsANote()
    {
        var agent = new HostAgent(
            new ScriptedBackend(), new FakeHost("web-01"), Fixtures.Settings, new StandingAnswer(true));

        agent.Say("Commands, handled here.");

        agent.Entries.OfType<TranscriptEntry.Note>().Single().Text.ShouldBe("Commands, handled here.");
    }
}
