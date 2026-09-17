using Avalonia.Controls;
using Avalonia.Input;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// Snippets: what the library holds, what the palette offers, and what reaches
/// the shell.
///
/// The last of those is the one with consequences. A snippet is typed into a
/// live server and run, so what is sent has to be exactly what the preview said
/// and nothing at all when the dialog is dismissed.
/// </summary>
public class SnippetTests
{
    /// <summary>A channel that keeps what was written, which is what "ran" means here.</summary>
    private sealed class RecordingChannel : ITerminalChannel
    {
        public MemoryStream Written { get; } = new();

        public Stream Stream => Written;

        public string Text => System.Text.Encoding.UTF8.GetString(Written.ToArray());

        public void Resize(int columns, int rows) { }

        public void Dispose() => Written.Dispose();
    }

    /// <summary>An empty home directory: enough to open a browser pane beside a shell.</summary>
    private sealed class EmptyFiles : StrangeSharpTerm.Transport.IRemoteFiles
    {
        public string Home => "/home/ops";

        public IReadOnlyList<StrangeSharpTerm.Transport.RemoteEntry> List(string path) => [];

        public void Download(string remotePath, string localPath) { }

        public void Upload(string localPath, string remotePath) { }

        public void Delete(StrangeSharpTerm.Transport.RemoteEntry entry) { }

        public void Rename(string path, string newPath) { }

        public void CreateDirectory(string path) { }

        public StrangeSharpTerm.Transport.RemoteEntry? Stat(string path) => null;

        public byte[] Read(string path, long limit) => [];

        public void Write(string path, byte[] content) { }
    }

    private sealed class Fixture
    {
        public Folder Production { get; } = new() { Name = "Production" };

        public Connection Host { get; }

        public Connection Elsewhere { get; } = new() { Name = "home-nas", Hostname = "192.168.1.20" };

        public RecordingChannel Channel { get; } = new();

        public ScriptedDialogService Dialogs { get; }

        public ShellViewModel Shell { get; }

        public FakeSessions Sessions { get; }

        public Fixture(ScriptedDialogService? dialogs = null, params Snippet[] snippets)
        {
            Host = new Connection { Name = "web-01", Hostname = "web-01.example.com", ParentId = Production.Id };
            Dialogs = dialogs ?? new ScriptedDialogService();
            Sessions = new FakeSessions { OnShell = _ => new TerminalSession(Channel), OnFiles = _ => new EmptyFiles() };
            var tree = new InventoryTree([Production], [Host, Elsewhere], snippets: snippets);
            Shell = new ShellViewModel(
                new InventoryViewModel(null, tree),
                Sessions,
                (_, _) => new Border(),
                Dialogs,
                secrets: new InMemorySecretStore());
            Shell.Describe(KeyModifiers.Meta);
        }

        /// <summary>Selects the host and opens a shell on it, which is what a snippet needs.</summary>
        public async Task Open()
        {
            Shell.Inventory.Selection = Host.Id;
            await Shell.ConnectSelectedCommand.ExecuteAsync(null);
        }
    }

    private static Snippet Snippet(string name = "uptime", string command = "uptime", NodeId? folder = null) =>
        new() { Name = name, Command = command, FolderId = folder };

    // MARK: the form

    [Fact]
    public void ASnippetNeedsANameAndACommand()
    {
        var draft = SnippetDraft.New(new InventoryTree(), 0);

        draft.IsValid.ShouldBeFalse();

        draft.Name = "Tail the log";
        draft.IsValid.ShouldBeFalse();

        draft.Command = "tail -f /var/log/app.log";
        draft.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void AnUnclosedPlaceholderIsRefused()
    {
        // It would never be asked for, and a literal "{{service" would reach the
        // shell instead.
        var draft = SnippetDraft.New(new InventoryTree(), 0);
        draft.Name = "Tail the log";
        draft.Command = "tail -f /var/log/{{service.log";

        draft.IsValid.ShouldBeFalse();
        draft.ProblemSummary.ShouldContain("closing }}");
    }

    [Fact]
    public void TheFormReadsPlaceholdersBackOutOfTheCommand()
    {
        var draft = SnippetDraft.New(new InventoryTree(), 0);
        draft.Name = "Restart";
        draft.PlaceholderNote.ShouldContain("No placeholders");

        draft.Command = "systemctl restart {{service}} on {{host}}";

        draft.Placeholders.ShouldBe(["service", "host"]);
        draft.PlaceholderNote.ShouldBe("Asks for service, host before running.");
    }

    [Fact]
    public void AScopeIsAFolderOrEverywhere()
    {
        var folder = new Folder { Name = "Production" };
        var tree = new InventoryTree([folder]);

        var draft = SnippetDraft.New(tree, 0);
        // "Everywhere", not "No folder": for a snippet null is a scope, not an
        // absence.
        draft.Scopes.Select(choice => choice.Label).ShouldBe(["Everywhere", "Production"]);
        draft.Scope.Label.ShouldBe("Everywhere");

        draft.Name = "Deploy";
        draft.Command = "deploy";
        draft.Scope = draft.Scopes[1];

        draft.Applied().FolderId.ShouldBe(folder.Id);
    }

    [Fact]
    public void AnEditedSnippetKeepsItsIdAndPlace()
    {
        var snippet = Snippet() with { SortIndex = 3 };
        var draft = SnippetDraft.For(new InventoryTree(snippets: [snippet]), snippet);
        draft.Name = "Load average";

        var saved = draft.Applied();

        saved.Id.ShouldBe(snippet.Id);
        saved.SortIndex.ShouldBe(3);
        saved.Name.ShouldBe("Load average");
    }

    // MARK: the library

    [Fact]
    public async Task AddingOneListsItWithWhereItIsOffered()
    {
        var folder = new Folder { Name = "Production" };
        var inventory = new InventoryViewModel(null, new InventoryTree([folder]));
        var dialogs = new ScriptedDialogService
        {
            EditSnippet = draft =>
            {
                draft.Name = "Deploy";
                draft.Command = "deploy {{version}}";
                draft.Scope = draft.Scopes[1];
                return true;
            },
        };
        var library = new SnippetsViewModel(inventory, dialogs);

        await library.AddCommand.ExecuteAsync(null);

        var row = library.Rows.ShouldHaveSingleItem();
        row.Name.ShouldBe("Deploy");
        row.Command.ShouldBe("deploy {{version}}");
        row.Detail.ShouldBe("In Production · asks for version");
    }

    [Fact]
    public async Task ACancelledFormWritesNothing()
    {
        var library = new SnippetsViewModel(
            new InventoryViewModel(null, new InventoryTree()),
            new ScriptedDialogService { EditSnippet = draft => { draft.Name = "Never"; return false; } });

        await library.AddCommand.ExecuteAsync(null);

        library.Rows.ShouldBeEmpty();
        library.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void TheLibraryListsEveryOneWhereverItIsScoped()
    {
        // The palette narrows by scope; the library must not, or a snippet
        // scoped to a folder could never be edited from anywhere else.
        var folder = new Folder { Name = "Production" };
        var tree = new InventoryTree(
            [folder],
            snippets: [Snippet("everywhere"), Snippet("prod only", "deploy", folder.Id)]);
        var library = new SnippetsViewModel(new InventoryViewModel(null, tree), new ScriptedDialogService());

        library.Refresh();

        library.Rows.Select(row => row.Name).ShouldBe(["everywhere", "prod only"]);
        library.Rows[0].Detail.ShouldBe("Everywhere");
        library.Rows[1].Detail.ShouldBe("In Production");
    }

    [Fact]
    public async Task DeletingAsksAndShowsTheCommandItIsAbout()
    {
        // Nothing depends on a snippet, so the only question is whether this is
        // the right one — and the command is the answer to that.
        var snippet = Snippet("cleanup", "rm -rf /var/tmp/build");
        var inventory = new InventoryViewModel(null, new InventoryTree(snippets: [snippet]));
        var dialogs = new ScriptedDialogService(answer: true);
        var library = new SnippetsViewModel(inventory, dialogs);
        library.Refresh();
        library.Selected = library.Rows[0];

        await library.DeleteCommand.ExecuteAsync(null);

        dialogs.Asked.ShouldHaveSingleItem().ShouldBe(("Delete cleanup?", "rm -rf /var/tmp/build"));
        library.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedDeleteKeepsIt()
    {
        var inventory = new InventoryViewModel(null, new InventoryTree(snippets: [Snippet()]));
        var library = new SnippetsViewModel(inventory, new ScriptedDialogService(answer: false));
        library.Refresh();
        library.Selected = library.Rows[0];

        await library.DeleteCommand.ExecuteAsync(null);

        library.Rows.ShouldHaveSingleItem();
        inventory.Pending.ShouldBeNull();
    }

    [Fact]
    public void EditAndDeleteWaitForASelection()
    {
        var library = new SnippetsViewModel(
            new InventoryViewModel(null, new InventoryTree(snippets: [Snippet()])),
            new ScriptedDialogService());
        library.Refresh();

        library.EditCommand.CanExecute(null).ShouldBeFalse();
        library.DeleteCommand.CanExecute(null).ShouldBeFalse();

        library.Selected = library.Rows[0];

        library.EditCommand.CanExecute(null).ShouldBeTrue();
        library.DeleteCommand.CanExecute(null).ShouldBeTrue();
    }

    // MARK: the palette

    [Fact]
    public async Task ThePaletteOffersTheSnippetsThisHostIsInScopeFor()
    {
        var everywhere = Snippet("uptime");
        var fixture = new Fixture(null, everywhere);
        var scoped = Snippet("deploy", "deploy", fixture.Production.Id);
        fixture.Shell.Inventory.Upsert(scoped);
        await fixture.Open();

        fixture.Shell.OpenPaletteCommand.Execute(null);
        fixture.Shell.Palette.Query = "de";

        fixture.Shell.Palette.Matches.Select(match => match.Title).ShouldContain("deploy");

        // The other host is outside the folder, so the scoped one is not offered
        // there — and the unscoped one still is.
        fixture.Shell.Inventory.Selection = fixture.Elsewhere.Id;
        fixture.Shell.OpenPaletteCommand.Execute(null);

        var titles = fixture.Shell.Palette.Matches.Select(match => match.Title).ToArray();
        titles.ShouldContain("uptime");
        titles.ShouldNotContain("deploy");
    }

    [Fact]
    public void WithNoShellOpenNoSnippetIsOffered()
    {
        // A snippet needs somewhere to be typed. Offering one that cannot run is
        // what the palette exists not to do.
        var fixture = new Fixture(null, Snippet());

        fixture.Shell.OpenPaletteCommand.Execute(null);

        fixture.Shell.Palette.Matches.Select(match => match.Title).ShouldNotContain("uptime");
    }

    // MARK: running one

    [Fact]
    public async Task RunningOneSendsItWithACarriageReturn()
    {
        // Carriage return, not a line feed: a Unix pty translates one, Windows
        // does not, and a line feed there is typed and never run.
        var snippet = Snippet();
        var fixture = new Fixture(null, snippet);
        await fixture.Open();

        await fixture.Shell.RunSnippetCommand.ExecuteAsync(snippet);

        fixture.Channel.Text.ShouldBe("uptime\r");
    }

    [Fact]
    public async Task AParameterisedOneAsksFirstAndSendsWhatThePreviewSaid()
    {
        var snippet = Snippet("tail", "tail -f /var/log/{{service}}.log");
        var dialogs = new ScriptedDialogService
        {
            FillSnippet = filling =>
            {
                filling.IsComplete.ShouldBeFalse();
                filling.Values.Single().Value = "nginx";
                filling.Preview.ShouldBe("tail -f /var/log/nginx.log");
                filling.IsComplete.ShouldBeTrue();
                return true;
            },
        };
        var fixture = new Fixture(dialogs, snippet);
        await fixture.Open();

        await fixture.Shell.RunSnippetCommand.ExecuteAsync(snippet);

        fixture.Dialogs.Filled.ShouldHaveSingleItem();
        fixture.Channel.Text.ShouldBe("tail -f /var/log/nginx.log\r");
    }

    [Fact]
    public async Task DismissingTheDialogTypesNothingAtAll()
    {
        var snippet = Snippet("cleanup", "rm -rf {{path}}");
        var fixture = new Fixture(new ScriptedDialogService(answer: false), snippet);
        await fixture.Open();

        await fixture.Shell.RunSnippetCommand.ExecuteAsync(snippet);

        fixture.Channel.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASnippetWithoutAShellDoesNothing()
    {
        var snippet = Snippet();
        var fixture = new Fixture(null, snippet);

        fixture.Shell.CanRunSnippet(snippet).ShouldBeFalse();
        await fixture.Shell.RunSnippetCommand.ExecuteAsync(snippet);

        fixture.Channel.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task ABrowserPaneIsNotSomethingToTypeInto()
    {
        var snippet = Snippet();
        var fixture = new Fixture(null, snippet);
        await fixture.Open();

        // The browser opens beside the shell and takes the focus, and it is not
        // a shell.
        await fixture.Shell.BrowseFilesCommand.ExecuteAsync(null);

        fixture.Shell.Panes[^1].IsActive.ShouldBeTrue();
        fixture.Shell.CanRunSnippet(snippet).ShouldBeFalse();
    }

    [Fact]
    public async Task TheFillingDialogSaysWhenBroadcastWillFanItOut()
    {
        // A command meant for one server about to run on several is the surprise
        // worth preventing.
        var snippet = Snippet("tail", "tail -f /var/log/{{service}}.log");
        var seen = new List<SnippetRunViewModel>();
        var dialogs = new ScriptedDialogService { FillSnippet = filling => { seen.Add(filling); return false; } };
        var fixture = new Fixture(dialogs, snippet);
        await fixture.Open();
        await fixture.Shell.SplitRightCommand.ExecuteAsync(null);
        fixture.Shell.IsBroadcasting = true;

        await fixture.Shell.RunSnippetCommand.ExecuteAsync(snippet);

        var filling = seen.ShouldHaveSingleItem();
        filling.IsBroadcast.ShouldBeTrue();
        filling.PaneCount.ShouldBe(2);
        filling.BroadcastWarning.ShouldBe("Broadcast is on: this runs in all 2 panes of this tab.");
    }

    [Fact]
    public async Task TheDialogNamesTheHostRatherThanTheShellPrompt()
    {
        // The far end sets the pane's title, and "ops@ip-10-0-1-7:~" is not how
        // anyone identifies the server they are about to run something on.
        var snippet = Snippet("tail", "tail {{path}}");
        var seen = new List<SnippetRunViewModel>();
        var fixture = new Fixture(
            new ScriptedDialogService { FillSnippet = filling => { seen.Add(filling); return false; } },
            snippet);
        await fixture.Open();
        fixture.Shell.Workspace.UpdateTitle(fixture.Shell.Workspace.ActivePaneId!.Value, "ops@ip-10-0-1-7:~");

        await fixture.Shell.RunSnippetCommand.ExecuteAsync(snippet);

        seen.ShouldHaveSingleItem().Target.ShouldBe("web-01");
    }

    [Fact]
    public async Task WithBroadcastOnItReachesEveryPaneInTheTab()
    {
        // A snippet is sent rather than written, which is what puts it through
        // the same fan-out as typing. Nothing else makes that true.
        var snippet = Snippet();
        var second = new RecordingChannel();
        var channels = new Queue<RecordingChannel>();
        var fixture = new Fixture(null, snippet);
        await fixture.Open();

        // The split's shell is a second channel, so the two can be told apart.
        channels.Enqueue(second);
        fixture.Sessions.OnShell = _ => new TerminalSession(channels.Dequeue());
        await fixture.Shell.SplitRightCommand.ExecuteAsync(null);
        fixture.Shell.IsBroadcasting = true;

        await fixture.Shell.RunSnippetCommand.ExecuteAsync(snippet);

        // The focused pane is the split, and the first one hears it too.
        second.Text.ShouldBe("uptime\r");
        fixture.Channel.Text.ShouldBe("uptime\r");
    }

    [Fact]
    public async Task TheLibraryOpensFromTheWindow()
    {
        var fixture = new Fixture();

        await fixture.Shell.ManageSnippetsCommand.ExecuteAsync(null);

        fixture.Dialogs.ManagedSnippets.ShouldHaveSingleItem();
    }
}
