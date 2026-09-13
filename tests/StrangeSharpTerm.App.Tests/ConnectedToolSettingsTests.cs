using StrangeSharpTerm.App.Assistant;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Mcp;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.Tests;

public class ConnectedToolSettingsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"strangesharpterm-mcp-{Guid.NewGuid():N}");

    private string Preferences_ => Path.Combine(_directory, Preferences.FileName);

    public ConnectedToolSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static McpServerConfig Runbooks { get; } = new()
    {
        Name = "Runbooks",
        Command = "npx",
        Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "~/runbooks"],
    };

    [Fact]
    public void AServerSurvivesARelaunchAndKeepsItsGrants()
    {
        McpPreferences.Save(
            Preferences_,
            new McpSettings { Servers = [Runbooks with { AlwaysAllowed = ["read_file"] }], OfferInRuns = true });

        var back = McpPreferences.Load(Preferences_);

        back.Servers.ShouldHaveSingleItem().AlwaysAllowed.ShouldBe(["read_file"]);
        back.OfferInRuns.ShouldBeTrue();
    }

    [Fact]
    public void ConnectedToolsShareTheFileWithTheThemeAndTheAssistant()
    {
        new Preferences { Theme = "dracula" }.Save(Preferences_);
        AssistPreferences.Save(Preferences_, new Assist.AssistSettings { Model = "claude-opus-5" });

        McpPreferences.Save(Preferences_, new McpSettings { Servers = [Runbooks] });

        Preferences.Load(Preferences_).Theme.ShouldBe("dracula");
        AssistPreferences.Load(Preferences_).ModelName.ShouldBe("claude-opus-5");
        McpPreferences.Load(Preferences_).Servers.ShouldHaveSingleItem().Name.ShouldBe("Runbooks");
    }

    [Fact]
    public void TheSheetListsEveryConfiguredServerEvenBeforeAnythingConnects()
    {
        var sheet = Sheet(new McpHub(new McpSettings { Servers = [Runbooks] }));

        // A sheet that hid a server until it connected would look like it had
        // lost it.
        var row = sheet.Servers.ShouldHaveSingleItem();
        row.Name.ShouldBe("Runbooks");
        row.IsConnected.ShouldBeFalse();
        row.Summary.ShouldBe("not connected yet");
        // Not connected yet is not a failure, and must not be drawn as one.
        row.HasFailed.ShouldBeFalse();
    }

    [Fact]
    public void TheRowSaysWhatHasBeenGrantedAndCanTakeItBack()
    {
        var hub = new McpHub(new McpSettings { Servers = [Runbooks with { AlwaysAllowed = ["read_file"] }] });
        var sheet = Sheet(hub);

        sheet.Servers[0].Granted.ShouldBe("Runs without asking: read_file");

        sheet.RevokeGrantsCommand.Execute(sheet.Servers[0]);

        sheet.Servers[0].HasGrants.ShouldBeFalse();
        hub.Settings.Servers[0].AlwaysAllowed.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemovingAServerTakesItsGrantsAndItsTokensWithIt()
    {
        var keys = new InMemorySecretStore();
        var server = Runbooks with { AlwaysAllowed = ["read_file"] };
        McpTokens.Write(server, new McpCredentials(Bearer: "a-token"), keys);

        var hub = new McpHub(new McpSettings { Servers = [server] });
        var sheet = Sheet(hub, keys, new ScriptedDialogService(answer: true));

        await sheet.RemoveServerCommand.ExecuteAsync(sheet.Servers[0]);

        hub.Settings.Servers.ShouldBeEmpty();
        McpTokens.Read(server, keys).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task RemovingAServerAsksFirst()
    {
        var hub = new McpHub(new McpSettings { Servers = [Runbooks] });
        var dialogs = new ScriptedDialogService(answer: false);
        var sheet = Sheet(hub, dialogs: dialogs);

        await sheet.RemoveServerCommand.ExecuteAsync(sheet.Servers[0]);

        dialogs.Asked.ShouldHaveSingleItem();
        hub.Settings.Servers.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AddingAServerSavesItAndItsToken()
    {
        var keys = new InMemorySecretStore();
        var hub = new McpHub(new McpSettings());
        var dialogs = new ScriptedDialogService
        {
            EditServer = draft =>
            {
                draft.Name = "Grafana";
                draft.IsHttp = true;
                draft.Url = "https://metrics.example.com/mcp";
                draft.NewBearer = "a-token";
                return true;
            },
        };

        await Sheet(hub, keys, dialogs).AddServerCommand.ExecuteAsync(null);

        var saved = hub.Settings.Servers.ShouldHaveSingleItem();
        saved.Name.ShouldBe("Grafana");
        saved.Transport.ShouldBe(McpTransport.Http);
        // The token goes to the store, never into the settings file.
        McpTokens.Read(saved, keys).Bearer.ShouldBe("a-token");
    }

    [Fact]
    public void TheTwoSwitchesAreRememberedSeparately()
    {
        var hub = new McpHub(new McpSettings());
        var sheet = Sheet(hub);

        sheet.OfferInPanes.ShouldBeTrue();
        sheet.OfferInRuns.ShouldBeFalse();

        sheet.OfferInRuns = true;

        hub.Settings.OfferInRuns.ShouldBeTrue();
        hub.Settings.OfferInPanes.ShouldBeTrue();
    }

    [Fact]
    public void OnlyAHostedServerHasAnythingToSignInTo()
    {
        var hub = new McpHub(new McpSettings
        {
            Servers = [Runbooks, new McpServerConfig { Name = "Grafana", Transport = McpTransport.Http, Url = "https://x.example.com/mcp" }],
        });
        var sheet = Sheet(hub);

        sheet.Servers[0].CanSignIn.ShouldBeFalse();
        sheet.Servers[1].CanSignIn.ShouldBeTrue();
    }

    [Fact]
    public void ACommandLineIsSplitAsAPersonWroteIt()
    {
        var draft = McpServerDraft.New(new InMemorySecretStore());
        draft.Name = "Runbooks";
        draft.CommandLine = "npx -y @modelcontextprotocol/server-filesystem \"~/my runbooks\"";

        var applied = draft.Applied();

        applied.Command.ShouldBe("npx");
        // A path with a space in it needs the quoted run to stay together.
        applied.Arguments.ShouldBe(["-y", "@modelcontextprotocol/server-filesystem", "~/my runbooks"]);
    }

    [Fact]
    public void ADraftSaysWhyItCannotBeSaved()
    {
        var draft = McpServerDraft.New(new InMemorySecretStore());

        draft.CanSave.ShouldBeFalse();
        draft.Problem.ShouldBe("It needs a name.");

        draft.Name = "Runbooks";
        draft.Problem.ShouldBe("It needs a command to run.");

        draft.CommandLine = "npx";
        draft.CanSave.ShouldBeTrue();
    }

    [Fact]
    public void ADraftSaysWhenTheCommandIsNotThere()
    {
        var draft = McpServerDraft.New(new InMemorySecretStore());
        draft.Name = "Missing";
        draft.CommandLine = "definitely-not-installed-xyz --serve";

        // Said before it is tried, because an app launched from Finder inherits
        // launchd's PATH rather than a shell's.
        draft.CommandProblem.ShouldNotBeNull().ShouldContain("definitely-not-installed-xyz");
    }

    [Fact]
    public void EditingAServerKeepsItsIdentityAndItsGrants()
    {
        var server = Runbooks with { AlwaysAllowed = ["read_file"] };
        var draft = McpServerDraft.For(server, new InMemorySecretStore());

        draft.Name = "Runbooks (new)";
        var applied = draft.Applied();

        applied.Id.ShouldBe(server.Id);
        applied.AlwaysAllowed.ShouldBe(["read_file"]);
    }

    private SettingsViewModel Sheet(
        McpHub hub,
        ISecretStore? keys = null,
        IDialogService? dialogs = null) =>
        new(new AppTheme(),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            tools: hub,
            dialogs: dialogs ?? new ScriptedDialogService(),
            toolKeys: keys ?? new InMemorySecretStore());
}
