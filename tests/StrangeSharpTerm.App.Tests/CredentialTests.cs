using Avalonia.Controls;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The credential library: what the form writes, where the secret goes, and what
/// the list says about a credential before you delete it.
///
/// The line these tests hold is that the inventory and the secret store are
/// separate. A credential is an ordinary JSON record; a password is the
/// operating system's business. Any test here that finds a secret in the
/// inventory is a leak.
/// </summary>
public class CredentialTests
{
    /// <summary>A store that refuses, the way a locked keychain does.</summary>
    private sealed class ShutStore : ISecretStore
    {
        public string? Secret(string account) => throw new SecretStoreException("The keychain is locked.");

        public void SetSecret(string account, string secret) =>
            throw new SecretStoreException("The keychain is locked.");

        public void RemoveSecret(string account) => throw new SecretStoreException("The keychain is locked.");

        public bool HasSecret(string account) => throw new SecretStoreException("The keychain is locked.");
    }

    private static CredentialsViewModel Library(
        ISecretStore secrets,
        IDialogService dialogs,
        InventoryTree? tree = null) =>
        new(new InventoryViewModel(null, tree ?? new InventoryTree()), secrets, dialogs);

    [Fact]
    public void ANewCredentialNeedsAName()
    {
        var draft = CredentialDraft.New(new InMemorySecretStore(), 0);

        draft.IsValid.ShouldBeFalse();

        draft.Name = "Production key";

        draft.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void AKeyCredentialNeedsTheKey()
    {
        var draft = CredentialDraft.New(new InMemorySecretStore(), 0);
        draft.Name = "Production key";
        draft.Method = CredentialMethod.IdentityFile;

        draft.IsValid.ShouldBeFalse();
        draft.ProblemSummary.ShouldContain("path to the key");

        draft.IdentityFile = "~/.ssh/id_ed25519";

        draft.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void APasswordCredentialWithNoPasswordCannotAuthenticateAndIsRefusedNow()
    {
        // Better here than at connect time, where the refusal arrives from the
        // far end and reads like the server's fault.
        var draft = CredentialDraft.New(new InMemorySecretStore(), 0);
        draft.Name = "Shared account";
        draft.Method = CredentialMethod.Password;

        draft.IsValid.ShouldBeFalse();

        draft.Secret = "hunter2";

        draft.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void AnAgentCredentialAsksForNeitherAFileNorASecret()
    {
        var draft = CredentialDraft.New(new InMemorySecretStore(), 0);
        draft.Name = "Agent";

        draft.WantsIdentityFile.ShouldBeFalse();
        draft.WantsSecret.ShouldBeFalse();
        draft.SecretNote.ShouldContain("agent holds the key");
        draft.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void TheSecretGoesToTheStoreAndTheCredentialGoesToTheInventory()
    {
        var secrets = new InMemorySecretStore();
        var draft = CredentialDraft.New(secrets, 0);
        draft.Name = "Shared account";
        draft.Username = "ops";
        draft.Method = CredentialMethod.Password;
        draft.Secret = "hunter2";

        var saved = draft.Applied();
        draft.SaveSecret(saved);

        saved.Name.ShouldBe("Shared account");
        saved.Username.ShouldBe("ops");
        secrets.Secret(saved.SecretAccount).ShouldBe("hunter2");

        // Nothing anywhere in the record says the password. This is the leak
        // check: the inventory is written to disk in the clear.
        System.Text.Json.JsonSerializer.Serialize(saved).ShouldNotContain("hunter2");
    }

    [Fact]
    public void AnEmptySecretFieldLeavesTheStoredOneAlone()
    {
        // The field starts empty every time because showing the stored secret
        // would put it in a screenshot. Empty therefore has to mean "keep".
        var credential = new Credential { Name = "Shared account", Method = CredentialMethod.Password };
        var secrets = new InMemorySecretStore();
        secrets.SetSecret(credential.SecretAccount, "hunter2");

        var draft = CredentialDraft.For(credential, secrets);
        draft.HadSecret.ShouldBeTrue();
        draft.SecretNote.ShouldContain("leave this empty to keep it");
        draft.Name = "Renamed";

        draft.SaveSecret(draft.Applied());

        secrets.Secret(credential.SecretAccount).ShouldBe("hunter2");
    }

    [Fact]
    public void RenamingDoesNotOrphanTheSecret()
    {
        // The account is keyed by id, not name — this is the test that says so.
        var credential = new Credential { Name = "Old name", Method = CredentialMethod.Password };
        var secrets = new InMemorySecretStore();
        secrets.SetSecret(credential.SecretAccount, "hunter2");

        var draft = CredentialDraft.For(credential, secrets);
        draft.Name = "New name";
        var saved = draft.Applied();

        saved.SecretAccount.ShouldBe(credential.SecretAccount);
        secrets.HasSecret(saved.SecretAccount).ShouldBeTrue();
    }

    [Fact]
    public void ForgettingRemovesIt()
    {
        var credential = new Credential { Name = "Shared account", Method = CredentialMethod.Password };
        var secrets = new InMemorySecretStore();
        secrets.SetSecret(credential.SecretAccount, "hunter2");

        var draft = CredentialDraft.For(credential, secrets);
        draft.ForgetSecret = true;
        draft.SaveSecret(draft.Applied());

        secrets.HasSecret(credential.SecretAccount).ShouldBeFalse();
    }

    [Fact]
    public void SwitchingToTheAgentThrowsTheOldSecretAway()
    {
        // Otherwise it lingers in the keychain under an account nothing points
        // at, where nobody will ever think to clear it.
        var credential = new Credential { Name = "Was a password", Method = CredentialMethod.Password };
        var secrets = new InMemorySecretStore();
        secrets.SetSecret(credential.SecretAccount, "hunter2");

        var draft = CredentialDraft.For(credential, secrets);
        draft.Method = CredentialMethod.Agent;
        draft.SaveSecret(draft.Applied());

        secrets.HasSecret(credential.SecretAccount).ShouldBeFalse();
    }

    [Fact]
    public async Task AddingOneListsIt()
    {
        var secrets = new InMemorySecretStore();
        var dialogs = new ScriptedDialogService
        {
            EditCredential = draft =>
            {
                draft.Name = "Production key";
                draft.Method = CredentialMethod.IdentityFile;
                draft.IdentityFile = "~/.ssh/id_ed25519";
                draft.Secret = "passphrase";
                return true;
            },
        };
        var library = Library(secrets, dialogs);

        await library.AddCommand.ExecuteAsync(null);

        var row = library.Rows.ShouldHaveSingleItem();
        row.Name.ShouldBe("Production key");
        row.Method.ShouldBe("Key file");
        row.Detail.ShouldContain("~/.ssh/id_ed25519");
        row.Detail.ShouldContain("secret stored");
        row.Usage.ShouldBe("used by nothing");
        library.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task ACancelledFormWritesNothingAnywhere()
    {
        var secrets = new InMemorySecretStore();
        var dialogs = new ScriptedDialogService
        {
            EditCredential = draft =>
            {
                draft.Name = "Never saved";
                draft.Method = CredentialMethod.Password;
                draft.Secret = "hunter2";
                return false;
            },
        };
        var library = Library(secrets, dialogs);

        await library.AddCommand.ExecuteAsync(null);

        library.Rows.ShouldBeEmpty();
        secrets.HasSecret($"credential-{NodeId.New()}").ShouldBeFalse();
        library.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AnAgentCredentialHasNoSecondLineToDraw()
    {
        // It has no username of its own, no file and no secret, and an empty
        // line still takes up a line.
        var row = new CredentialRow(new Credential { Name = "Personal agent" }, 0, HasSecret: false);

        row.Detail.ShouldBeEmpty();
        row.HasDetail.ShouldBeFalse();
    }

    [Fact]
    public void TheListSaysHowManyThingsWouldBreak()
    {
        // The answer to "can I delete this" is a number, so the row carries it.
        var credential = new Credential { Name = "Production key" };
        var settings = new ConnectionSettings { CredentialId = credential.Id };
        var tree = new InventoryTree(
            folders: [new Folder { Name = "Production", Settings = settings }],
            connections:
            [
                new Connection { Name = "web-01", Hostname = "web-01.example.com", Settings = settings },
                new Connection { Name = "web-02", Hostname = "web-02.example.com" },
            ],
            credentials: [credential]);
        var library = Library(new InMemorySecretStore(), new ScriptedDialogService(), tree);

        library.Refresh();

        library.Rows.ShouldHaveSingleItem().Usage.ShouldBe("used by 2 hosts and folders");
    }

    [Fact]
    public async Task DeletingAsksFirstAndSaysWhatIsLeftPointingAtNothing()
    {
        var credential = new Credential { Name = "Production key" };
        var tree = new InventoryTree(
            connections:
            [
                new Connection
                {
                    Name = "web-01",
                    Hostname = "web-01.example.com",
                    Settings = new ConnectionSettings { CredentialId = credential.Id },
                },
            ],
            credentials: [credential]);
        var dialogs = new ScriptedDialogService(answer: false);
        var secrets = new InMemorySecretStore();
        secrets.SetSecret(credential.SecretAccount, "hunter2");
        var library = Library(secrets, dialogs, tree);
        library.Refresh();
        library.Selected = library.Rows[0];

        await library.DeleteCommand.ExecuteAsync(null);

        var asked = dialogs.Asked.ShouldHaveSingleItem();
        asked.Title.ShouldBe("Delete Production key?");
        asked.Detail.ShouldContain("1 host or folder");

        // Said no: both the credential and its secret are still there.
        library.Rows.ShouldHaveSingleItem();
        secrets.HasSecret(credential.SecretAccount).ShouldBeTrue();
    }

    [Fact]
    public async Task DeletingTakesTheSecretWithIt()
    {
        var credential = new Credential { Name = "Production key", Method = CredentialMethod.Password };
        var secrets = new InMemorySecretStore();
        secrets.SetSecret(credential.SecretAccount, "hunter2");
        var library = Library(
            secrets,
            new ScriptedDialogService(answer: true),
            new InventoryTree(credentials: [credential]));
        library.Refresh();
        library.Selected = library.Rows[0];

        await library.DeleteCommand.ExecuteAsync(null);

        library.Rows.ShouldBeEmpty();
        // An account nothing points at is one nobody can clear later.
        secrets.HasSecret(credential.SecretAccount).ShouldBeFalse();
    }

    [Fact]
    public void AStoreThatWillNotOpenIsSaidRatherThanThrown()
    {
        var credential = new Credential { Name = "Production key", Method = CredentialMethod.Password };
        var library = Library(new ShutStore(), new ScriptedDialogService(), new InventoryTree(credentials: [credential]));

        library.Refresh();

        library.Rows.ShouldHaveSingleItem().HasSecret.ShouldBeFalse();
        library.Failure.ShouldBe("The keychain is locked.");
    }

    [Fact]
    public async Task ACredentialIsSavedEvenWhenItsSecretCannotBe()
    {
        // The inventory is ours and the store is the operating system's. Losing
        // the second must not lose the first, or a locked keychain eats the work.
        var dialogs = new ScriptedDialogService
        {
            EditCredential = draft =>
            {
                draft.Name = "Shared account";
                draft.Method = CredentialMethod.Password;
                draft.Secret = "hunter2";
                return true;
            },
        };
        var library = Library(new ShutStore(), dialogs);

        await library.AddCommand.ExecuteAsync(null);

        library.Rows.ShouldHaveSingleItem().Name.ShouldBe("Shared account");
        library.Failure.ShouldBe("The keychain is locked.");
    }

    [Fact]
    public void TheDetailPaneSaysWhichCredentialAnswered()
    {
        // Otherwise the username and keys it shows appear from nowhere: they are
        // the credential's, and nothing else on the pane says so.
        var credential = new Credential
        {
            Name = "Ops key",
            Username = "ops",
            Method = CredentialMethod.IdentityFile,
            IdentityFile = "~/.ssh/id_ed25519",
        };
        var folder = new Folder { Name = "Production", Settings = new ConnectionSettings { CredentialId = credential.Id } };
        var host = new Connection { Name = "web-01", Hostname = "web-01.example.com", ParentId = folder.Id };
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree([folder], [host], credentials: [credential])),
            new FakeSessions(),
            (_, _) => new Border(),
            new ScriptedDialogService(),
            secrets: new InMemorySecretStore());

        shell.Inventory.Selection = host.Id;

        var fields = shell.Detail.ShouldNotBeNull().Fields;
        // Inherited from the folder, and still named on the host beneath it.
        fields.ShouldContain(field => field.Label == "Credential" && field.Value == "Ops key (Key file)");
        fields.ShouldContain(field => field.Label == "Username" && field.Value == "ops");
    }

    [Fact]
    public async Task TheLibraryOpensFromTheWindow()
    {
        var dialogs = new ScriptedDialogService();
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree()),
            new FakeSessions(),
            (_, _) => new Border(),
            dialogs,
            secrets: new InMemorySecretStore());

        await shell.ManageCredentialsCommand.ExecuteAsync(null);

        dialogs.Managed.ShouldHaveSingleItem();
    }
}
