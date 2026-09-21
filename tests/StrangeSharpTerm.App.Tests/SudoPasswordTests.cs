using StrangeSharpTerm.App.Assistant;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.Tests;

/// <summary>A server that records every command it is given, and what was written to each one's input.</summary>
internal sealed class RecordingCommands : IRemoteCommands
{
    public List<(string Command, string? Input)> Ran { get; } = [];

    /// <summary>What <c>sudo -n true</c> answers: 0 where sudo needs no password.</summary>
    public int ProbeStatus { get; set; } = 1;

    public CommandResult Run(string command, TimeSpan timeout)
    {
        Ran.Add((command, null));
        return command == Sudo.Probe ? new CommandResult(ProbeStatus, "", "") : new CommandResult(0, "ok", "");
    }

    public CommandResult RunFeeding(string command, TimeSpan timeout, string input)
    {
        Ran.Add((command, input));
        return new CommandResult(0, "ok", "");
    }
}

/// <summary>A provider that says what it was told to, and keeps every request it was handed.</summary>
internal sealed class Scripted(params IReadOnlyList<AssistEvent>[] turns) : IAssistBackend
{
    private int _turn;

    public string ProviderName => "Scripted";

    public string Model => "scripted-1";

    public List<AssistRequest> Requests { get; } = [];

    public static IReadOnlyList<AssistEvent> Says(string text) =>
        [new AssistEvent.Say(text), new AssistEvent.Finished(AssistStop.EndTurn)];

    public static IReadOnlyList<AssistEvent> Runs(string command) =>
    [
        new AssistEvent.Call(new AssistToolCall(
            "call_1",
            AssistTools.RunCommand,
            System.Text.Json.JsonSerializer.Serialize(new { command, why = "because" }))),
        new AssistEvent.Finished(AssistStop.ToolUse),
    ];

    public async IAsyncEnumerable<AssistEvent> Stream(
        AssistRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var turn = _turn < turns.Length ? turns[_turn] : Says("Nothing more.");
        _turn++;
        foreach (var streamed in turn)
        {
            await Task.Yield();
            yield return streamed;
        }
    }
}

/// <summary>
/// Handing a host's password to sudo: when it happens, how, and that it goes
/// nowhere else.
/// </summary>
public class SudoPasswordTests
{
    private const string Password = "hunter2-correct-horse";

    private sealed class NoHealth : IServerHealth
    {
        public ServerMetrics Collect() => new();
    }

    private static SshHostAccess Access(RecordingCommands commands, string? password) =>
        new("web-01", commands, new NoHealth(), () => null, () => password);

    private static async Task Run(SshHostAccess access, string command) =>
        await access.Run(command, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    [Fact]
    public async Task WithTheSwitchOffSudoRunsAsItAlwaysDid()
    {
        var commands = new RecordingCommands();

        await Run(Access(commands, password: null), "sudo apt-get install -y nginx");

        // No probe, no feeding: exactly the old behaviour, which sudo then
        // fails with "a terminal is required".
        commands.Ran.ShouldBe([("sudo apt-get install -y nginx", null)]);
    }

    [Fact]
    public async Task WhereSudoWantsAPasswordItIsGivenOnTheInput()
    {
        var commands = new RecordingCommands { ProbeStatus = 1 };

        await Run(Access(commands, Password), "sudo apt-get install -y nginx");

        commands.Ran.ShouldBe([
            (Sudo.Probe, null),
            ("sudo -k -S -p '' apt-get install -y nginx", Password + "\n"),
        ]);
    }

    [Fact]
    public async Task WhereSudoWantsNoPasswordNoneIsSent()
    {
        // The case -k cannot close. Sent anyway, sudo would not read it and the
        // command after it would -- sudo tee /etc/x would write it into the
        // file. So it is asked first, and nothing is sent.
        var commands = new RecordingCommands { ProbeStatus = 0 };

        await Run(Access(commands, Password), "sudo tee /etc/motd");

        commands.Ran.ShouldBe([(Sudo.Probe, null), ("sudo tee /etc/motd", null)]);
        commands.Ran.ShouldNotContain(ran => ran.Input != null);
    }

    [Theory]
    [InlineData("sudo apt-get update && sudo apt-get install -y nginx")]
    [InlineData("sudo cat /etc/shadow | tee out")]
    [InlineData("apt-get install -y nginx")]
    [InlineData("sudo -n whoami")]
    public async Task ACommandTheRuleRefusesIsRunUntouchedAndGetsNothing(string command)
    {
        var commands = new RecordingCommands();

        await Run(Access(commands, Password), command);

        commands.Ran.ShouldBe([(command, null)]);
    }

    [Fact]
    public async Task ASudoCommandThatDoesNotQualifyIsToldWhySoItCanBeSplit()
    {
        // Otherwise the model sees "a terminal is required" and has no way to
        // know that running the two halves separately would work.
        var commands = new RecordingCommands();

        var outcome = await Access(commands, Password).Run(
            "sudo apt-get update && sudo apt-get install -y nginx",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        outcome.Output.ShouldContain("Split it into one command per sudo");
    }

    [Theory]
    [InlineData("apt-get install -y nginx")]
    [InlineData("echo sudo")]
    public async Task ACommandWithoutSudoIsNotLecturedAboutIt(string command)
    {
        var commands = new RecordingCommands();

        var outcome = await Access(commands, Password).Run(
            command, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        outcome.Output.ShouldNotContain("Split it");
    }

    [Fact]
    public async Task WithTheSwitchOffNothingIsSaidAboutIt()
    {
        // No password to give, so nothing about giving one: sudo's own error is
        // the whole story, and it is the one it always was.
        var commands = new RecordingCommands();

        var outcome = await Access(commands, password: null).Run(
            "sudo apt-get update && sudo apt-get install -y nginx",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        outcome.Output.ShouldNotContain("Split it");
    }

    [Fact]
    public async Task ThePasswordIsNeverInAnyCommandThatRan()
    {
        var commands = new RecordingCommands { ProbeStatus = 1 };

        await Run(Access(commands, Password), "sudo systemctl restart nginx");

        // On the input and nowhere else: never in a command line, where ps on
        // the server would show it to anybody logged in.
        commands.Ran.ShouldAllBe(ran => !ran.Command.Contains(Password));
    }

    /// <summary>
    /// The whole way through an assistant turn, and the one that matters most.
    ///
    /// The password reaches sudo, and appears in nothing a model, a person or a
    /// log reads: not in the transcript, not in what goes back to the provider,
    /// not in what the pane beside the host is told.
    /// </summary>
    [Fact]
    public async Task ThroughAWholeTurnThePasswordReachesSudoAndNothingElse()
    {
        var commands = new RecordingCommands { ProbeStatus = 1 };
        var backend = new Scripted(
            Scripted.Runs("sudo systemctl restart nginx"),
            Scripted.Says("Restarted."));
        var agent = new HostAgent(backend, Access(commands, Password), new AssistSettings(), new StandingAnswer(true));
        var narrated = new List<AssistStep>();
        agent.Working += (_, step) => narrated.Add(step);

        await agent.Ask(
            "restart nginx",
            new AskOptions { MayRunCommands = true },
            TestContext.Current.CancellationToken);

        // It reached sudo.
        commands.Ran.ShouldContain(("sudo -k -S -p '' systemctl restart nginx", Password + "\n"));

        // And nowhere else.
        var transcript = string.Join('\n', agent.Entries.Select(entry => entry switch
        {
            TranscriptEntry.Step step => $"{step.Command} {step.Why} {step.Gate} {step.Detail} {step.Output}",
            TranscriptEntry.Answer answer => answer.Markdown,
            TranscriptEntry.Question question => question.Text,
            TranscriptEntry.Note note => note.Text,
            _ => "",
        }));
        transcript.ShouldNotContain(Password);

        var sentToProvider = string.Join('\n', backend.Requests.SelectMany(request =>
            request.Messages.Select(message =>
                $"{message.Text} {string.Join(' ', message.ToolResults.Select(result => result.Output))}"
                + string.Join(' ', message.ToolCalls.Select(call => call.Arguments)))
            .Append(request.System)));
        sentToProvider.ShouldNotContain(Password);

        narrated.ShouldAllBe(step => !step.Command.Contains(Password) && !step.Output.Contains(Password));

        // What the transcript shows is what the model asked for, not the rewrite.
        agent.Entries.OfType<TranscriptEntry.Step>().Single().Command.ShouldBe("sudo systemctl restart nginx");
    }
}

/// <summary>
/// The lookup, from the switch through the credential to the store.
///
/// The test the handoff says was missing once: one that follows a secret from
/// where it is written to where it is read.
/// </summary>
public class SudoPasswordLookupTests
{
    private static (InventoryTree Tree, Connection Host, InMemorySecretStore Store) Arrange(
        CredentialMethod method, bool onTheFolder = false)
    {
        var credential = new Credential { Name = "k8s", Method = method, Username = "ops" };
        var folder = new Folder
        {
            Name = "Cluster",
            Settings = onTheFolder ? new ConnectionSettings { CredentialId = credential.Id } : ConnectionSettings.Empty,
        };
        var host = new Connection
        {
            Name = "web-01",
            Hostname = "10.0.0.1",
            ParentId = folder.Id,
            Settings = onTheFolder ? ConnectionSettings.Empty : new ConnectionSettings { CredentialId = credential.Id },
        };
        var tree = new InventoryTree([folder], [host], credentials: [credential]);

        var store = new InMemorySecretStore();
        store.SetSecret(credential.SecretAccount, "s3cret");
        return (tree, host, store);
    }

    [Fact]
    public void WithTheSwitchOnAndAPasswordCredentialItIsTheCredentialsSecret()
    {
        var (tree, host, store) = Arrange(CredentialMethod.Password);

        new SudoPasswords(() => tree, () => store, new HashSet<NodeId> { host.Id })
            .For(host.Id).ShouldBe("s3cret");
    }

    [Fact]
    public void AnInheritedPasswordIsFound()
    {
        var (tree, host, store) = Arrange(CredentialMethod.Password, onTheFolder: true);

        new SudoPasswords(() => tree, () => store, new HashSet<NodeId> { host.Id })
            .For(host.Id).ShouldBe("s3cret");
    }

    [Fact]
    public void WithTheSwitchOffThereIsNothing()
    {
        var (tree, host, store) = Arrange(CredentialMethod.Password);

        new SudoPasswords(() => tree, () => store, new HashSet<NodeId>()).For(host.Id).ShouldBeNull();
    }

    [Fact]
    public void AHostThatSignsInWithAKeyHasNothingToGive()
    {
        // The switch alone is not enough: a key has no password sudo can use,
        // and a host moved to one since the switch went on gives sudo nothing.
        var (tree, host, store) = Arrange(CredentialMethod.IdentityFile);

        new SudoPasswords(() => tree, () => store, new HashSet<NodeId> { host.Id }).For(host.Id).ShouldBeNull();
    }

    [Fact]
    public void FromThePreferenceWrittenToThePasswordRead()
    {
        // All the way: the switch saved to preferences.json, read back, and the
        // password found in the store it was written to.
        var directory = Path.Combine(Path.GetTempPath(), $"sudo-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "preferences.json");
        try
        {
            var (tree, host, store) = Arrange(CredentialMethod.Password);

            SudoPreferences.Save(path, host.Id, allowed: true);
            new SudoPasswords(() => tree, () => store, SudoPreferences.Load(path)).For(host.Id).ShouldBe("s3cret");

            SudoPreferences.Save(path, host.Id, allowed: false);
            new SudoPasswords(() => tree, () => store, SudoPreferences.Load(path)).For(host.Id).ShouldBeNull();

            // And the password itself is nowhere in the file.
            File.ReadAllText(path).ShouldNotContain("s3cret");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>What the editor's switch offers, for a host that has a password and one that does not.</summary>
public class SudoSwitchTests
{
    private static (InventoryTree Tree, Connection Host) Arrange(CredentialMethod method, bool onTheFolder)
    {
        var credential = new Credential { Name = "k8s", Method = method, Username = "ops" };
        var folder = new Folder
        {
            Name = "Cluster",
            Settings = onTheFolder ? new ConnectionSettings { CredentialId = credential.Id } : ConnectionSettings.Empty,
        };
        var host = new Connection
        {
            Name = "web-01",
            Hostname = "10.0.0.1",
            ParentId = folder.Id,
            Settings = onTheFolder ? ConnectionSettings.Empty : new ConnectionSettings { CredentialId = credential.Id },
        };
        return (new InventoryTree([folder], [host], credentials: [credential]), host);
    }

    [Theory]
    [InlineData(CredentialMethod.Password, false, true)]
    [InlineData(CredentialMethod.Password, true, true)]
    [InlineData(CredentialMethod.IdentityFile, false, false)]
    [InlineData(CredentialMethod.Agent, true, false)]
    public void TheSwitchIsOfferedOnlyWhereThereIsAPasswordToGive(
        CredentialMethod method, bool onTheFolder, bool offered)
    {
        var (tree, host) = Arrange(method, onTheFolder);

        HostDraft.For(tree, host).SignsInWithAPassword.ShouldBe(offered);
    }

    [Fact]
    public void ItIsNotPartOfWhatTheInventoryIsGiven()
    {
        // The inventory stays byte-identical to the Swift app's, so the answer
        // lives beside it in preferences and never reaches Applied().
        var (tree, host) = Arrange(CredentialMethod.Password, onTheFolder: false);
        var off = HostDraft.For(tree, host);
        var on = HostDraft.For(tree, host);

        on.GivesSudoThePassword = true;

        // Compared as what would be written, because Applied() builds new lists
        // and a record compares lists by reference.
        System.Text.Json.JsonSerializer.Serialize(on.Applied())
            .ShouldBe(System.Text.Json.JsonSerializer.Serialize(off.Applied()));
    }
}
