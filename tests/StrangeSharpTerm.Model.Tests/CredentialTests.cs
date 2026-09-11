namespace StrangeSharpTerm.Model.Tests;

public class CredentialTests
{
    /// <summary>One credential, two hosts in different folders: the arrangement the feature exists for.</summary>
    private static (InventoryTree Tree, Credential Credential, Connection Inside, Connection Outside) Arrange()
    {
        var credential = new Credential
        {
            Name = "Ops key", Username = "ops", Method = CredentialMethod.IdentityFile,
            IdentityFile = "/keys/ops_ed25519",
        };
        var production = new Folder
        {
            Name = "Production", Settings = new ConnectionSettings { CredentialId = credential.Id },
        };
        var inside = new Connection { ParentId = production.Id, Name = "web-01", Hostname = "web-01" };
        var outside = new Connection { Name = "loose", Hostname = "loose" };

        return (new InventoryTree([production], [inside, outside], credentials: [credential]), credential, inside, outside);
    }

    [Fact]
    public void ACredentialOnAFolderCoversEveryHostBeneathIt()
    {
        var f = Arrange();
        var resolved = f.Tree.Resolve(f.Inside.Id);

        resolved.Settings.CredentialId.ShouldBe(f.Credential.Id);
        resolved.Settings.Username.ShouldBe("ops");
        resolved.Settings.IdentityFiles.ShouldBe(new[] { "/keys/ops_ed25519" });
    }

    [Fact]
    public void AHostOutsideTheFolderIsUnaffected()
    {
        var f = Arrange();
        var resolved = f.Tree.Resolve(f.Outside.Id);

        resolved.Settings.CredentialId.ShouldBeNull();
        resolved.Settings.Username.ShouldBeNull();
    }

    [Fact]
    public void AHostsOwnUsernameBeatsTheSharedCredentials()
    {
        // A shared credential is a default for the hosts that use it, not an
        // override of the ones that were explicit.
        var f = Arrange();
        var host = f.Inside with { Settings = f.Inside.Settings with { Username = "deploy" } };
        f.Tree.Upsert(host);

        var resolved = f.Tree.Resolve(host.Id);
        resolved.Settings.Username.ShouldBe("deploy");
        // The key still comes from the credential.
        resolved.Settings.IdentityFiles.ShouldBe(new[] { "/keys/ops_ed25519" });
    }

    [Fact]
    public void AHostCanPointAtADifferentCredentialThanItsFolder()
    {
        var f = Arrange();
        var personal = new Credential { Name = "Personal", Username = "hector", Method = CredentialMethod.Agent };
        f.Tree.Upsert(personal);
        var host = f.Inside with { Settings = f.Inside.Settings with { CredentialId = personal.Id } };
        f.Tree.Upsert(host);

        var resolved = f.Tree.Resolve(host.Id);
        resolved.Settings.CredentialId.ShouldBe(personal.Id);
        resolved.Settings.Username.ShouldBe("hector");
        // An agent credential contributes no key file, and the folder's is not
        // borrowed: the host's choice of credential replaces it wholesale.
        resolved.Settings.IdentityFiles.ShouldBeEmpty();
    }

    [Fact]
    public void BothHostsSharingACredentialResolveToTheSameKey()
    {
        var credential = new Credential
        {
            Name = "Shared", Username = "ops", Method = CredentialMethod.IdentityFile, IdentityFile = "/keys/shared",
        };
        var a = new Connection { Name = "a", Hostname = "a", Settings = new ConnectionSettings { CredentialId = credential.Id } };
        var b = new Connection { Name = "b", Hostname = "b", Settings = new ConnectionSettings { CredentialId = credential.Id } };
        var tree = new InventoryTree(connections: [a, b], credentials: [credential]);

        tree.Resolve(a.Id).Settings.IdentityFiles.ShouldBe(new[] { "/keys/shared" });
        tree.Resolve(b.Id).Settings.IdentityFiles.ShouldBe(new[] { "/keys/shared" });
    }

    [Fact]
    public void ADanglingCredentialReferenceIsIgnoredRatherThanFatal()
    {
        // A credential can be deleted while hosts still name it; that must degrade
        // to "no credential", not break the host.
        var host = new Connection
        {
            Name = "h", Hostname = "h", Settings = new ConnectionSettings { CredentialId = NodeId.New() },
        };
        var tree = new InventoryTree(connections: [host]);

        tree.Resolve(host.Id).Settings.Username.ShouldBeNull();
        tree.Resolve(host.Id).Settings.IdentityFiles.ShouldBeEmpty();
    }

    [Fact]
    public void DeletingACredentialCanReportWhatPointsAtIt()
    {
        var f = Arrange();
        var users = f.Tree.UsersOfCredential(f.Credential.Id);

        users.Folders.Select(folder => folder.Name).ShouldBe(new[] { "Production" });
        users.Connections.ShouldBeEmpty();
    }

    [Fact]
    public void OnlyMethodsThatNeedASecretSaySo()
    {
        new Credential { Name = "a", Method = CredentialMethod.Agent }.CanHaveSecret.ShouldBeFalse();
        new Credential { Name = "b", Method = CredentialMethod.Password }.CanHaveSecret.ShouldBeTrue();
        // A key file may or may not be encrypted, so an empty passphrase is a
        // legitimate state rather than an incomplete one.
        new Credential { Name = "c", Method = CredentialMethod.IdentityFile }.CanHaveSecret.ShouldBeTrue();
    }

    [Fact]
    public void TheSecretAccountFollowsTheIdSoRenamingKeepsTheSecret()
    {
        var credential = new Credential { Name = "Before", Method = CredentialMethod.Password };
        var account = credential.SecretAccount;

        (credential with { Name = "After" }).SecretAccount.ShouldBe(account);
    }
}
