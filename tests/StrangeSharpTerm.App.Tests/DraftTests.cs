using System.Text;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Store;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The editors, as far as they can be tested without a window: what a form does
/// to a host or a folder when it is saved, and what it refuses to save at all.
/// </summary>
public class DraftTests
{
    private static Connection Host(string name = "web-01", ConnectionSettings? settings = null, NodeId? parent = null) =>
        new()
        {
            Name = name,
            Hostname = $"{name}.example.com",
            ParentId = parent,
            Settings = settings ?? ConnectionSettings.Empty,
        };

    [Fact]
    public void ASavedFormWithNothingTouchedChangesNothing()
    {
        // The round trip that matters: opening a host and pressing Save must not
        // rewrite it. Everything else here is a variation on this failing.
        var host = Host() with
        {
            Tags = ["production", "web"],
            Settings = new ConnectionSettings
            {
                Username = "ops",
                Port = 2222,
                ForwardAgent = true,
                HostKeyPolicy = HostKeyPolicy.Strict,
                IdentityFiles = ["~/.ssh/id_ed25519"],
                JumpHosts = ["bastion", "edge"],
                Environment = new Dictionary<string, string> { ["LANG"] = "en_US.UTF-8" },
            },
        };
        var tree = new InventoryTree(connections: [host]);

        // Compared as the file, not as the record: rewriting a list of identity
        // files into an equal one is a new array and so a different record, and
        // the thing that must not change is what lands on disk.
        AsWritten(HostDraft.For(tree, host).Applied()).ShouldBe(AsWritten(host));
    }

    /// <summary>The host exactly as the inventory would write it.</summary>
    private static string AsWritten(Connection connection) =>
        Encoding.UTF8.GetString(
            InventoryDocument.From(new InventoryTree(connections: [connection])).ToUtf8Json());

    [Fact]
    public void SettingsTheFormDoesNotShowSurviveBeingEditedAround()
    {
        // Port forwards, environment and the font size are real settings with no
        // field in this editor; the credential has one now, but this host points
        // at an id the library does not hold. Rebuilding the record instead of
        // folding onto it would delete them, quietly, on any edit.
        var credential = NodeId.New();
        var forward = new PortForward
        {
            Kind = PortForwardKind.Local,
            Name = "postgres",
            BindPort = 5432,
            DestinationHost = "localhost",
            DestinationPort = 5432,
        };
        var host = Host(settings: new ConnectionSettings
        {
            CredentialId = credential,
            TerminalFontSize = 15,
            Environment = new Dictionary<string, string> { ["TZ"] = "Europe/Madrid" },
            PortForwards = [forward],
        });
        var tree = new InventoryTree(connections: [host]);

        var draft = HostDraft.For(tree, host);
        draft.Name = "web-02";
        var saved = draft.Applied();

        saved.Name.ShouldBe("web-02");
        saved.Settings.CredentialId.ShouldBe(credential);
        saved.Settings.TerminalFontSize.ShouldBe(15);
        saved.Settings.Environment.ShouldNotBeNull()["TZ"].ShouldBe("Europe/Madrid");
        saved.Settings.PortForwards.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(forward);
    }

    [Fact]
    public void ABlankFieldMeansInheritedRatherThanEmpty()
    {
        // "" and null are different answers: null asks the folder, "" would be a
        // username of nothing at all.
        var host = Host(settings: new ConnectionSettings { Username = "ops", Port = 2222 });
        var tree = new InventoryTree(connections: [host]);

        var draft = HostDraft.For(tree, host);
        draft.Settings.Username = "   ";
        draft.Settings.Port = "";

        var settings = draft.Applied().Settings;
        settings.Username.ShouldBeNull();
        settings.Port.ShouldBeNull();
    }

    [Fact]
    public void AnEmptyKeyListIsInheritedRatherThanNoKeys()
    {
        // Same distinction, one level up: an empty list would override a folder's
        // keys with none, which is not what clearing a text box means.
        var host = Host(settings: new ConnectionSettings { IdentityFiles = ["~/.ssh/id_ed25519"] });
        var tree = new InventoryTree(connections: [host]);

        var draft = HostDraft.For(tree, host);
        draft.Settings.IdentityFiles = "\n  \n";

        draft.Applied().Settings.IdentityFiles.ShouldBeNull();
    }

    [Fact]
    public void KeysAreOnePerLineAndJumpHostsAreCommaSeparated()
    {
        var tree = new InventoryTree(connections: [Host()]);
        var draft = HostDraft.New(tree, null, 0);
        draft.Name = "web-01";
        draft.Hostname = "web-01.example.com";
        draft.Settings.IdentityFiles = " ~/.ssh/id_ed25519 \n~/.ssh/id_rsa\n\n";
        draft.Settings.JumpHosts = "bastion , edge";

        var settings = draft.Applied().Settings;
        settings.IdentityFiles.ShouldBe(["~/.ssh/id_ed25519", "~/.ssh/id_rsa"]);
        settings.JumpHosts.ShouldBe(["bastion", "edge"]);
    }

    [Fact]
    public void ATristateCarriesTheThirdState()
    {
        var host = Host(settings: new ConnectionSettings { ForwardAgent = true, Compression = false });
        var tree = new InventoryTree(connections: [host]);

        var draft = HostDraft.For(tree, host);
        draft.Settings.ForwardAgent.ShouldBe(Inheritable.Yes);
        draft.Settings.Compression.ShouldBe(Inheritable.No);

        draft.Settings.ForwardAgent = Inheritable.Inherited;

        draft.Applied().Settings.ForwardAgent.ShouldBeNull();
        draft.Applied().Settings.Compression.ShouldBe(false);
    }

    [Fact]
    public void TagsAreSplitTrimmedAndNormalised()
    {
        var tree = new InventoryTree();
        var draft = HostDraft.New(tree, null, 0);
        draft.Name = " web-01 ";
        draft.Hostname = " web-01.example.com ";
        draft.Tags = "production,  web , ";

        var saved = draft.Applied();
        saved.Name.ShouldBe("web-01");
        saved.Hostname.ShouldBe("web-01.example.com");
        saved.Tags.ShouldBe(["production", "web"]);
    }

    [Fact]
    public void AHostNeedsANameAndSomewhereToConnect()
    {
        var draft = HostDraft.New(new InventoryTree(), null, 0);

        draft.IsValid.ShouldBeFalse();
        draft.Problems().Count().ShouldBe(2);

        draft.Name = "web-01";
        draft.Hostname = "web-01.example.com";

        draft.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("22x", "whole number")]
    [InlineData("0", "between 1 and 65535")]
    [InlineData("70000", "between 1 and 65535")]
    public void APortThatCannotBeOneIsRefused(string port, string expected)
    {
        var draft = HostDraft.New(new InventoryTree(), null, 0);
        draft.Name = "web-01";
        draft.Hostname = "web-01.example.com";
        draft.Settings.Port = port;

        draft.IsValid.ShouldBeFalse();
        draft.ProblemSummary.ShouldContain(expected);
    }

    [Fact]
    public void AKeepAliveOfZeroIsAllowedBecauseItMeansOff()
    {
        var draft = HostDraft.New(new InventoryTree(), null, 0);
        draft.Name = "web-01";
        draft.Hostname = "web-01.example.com";
        draft.Settings.KeepAliveInterval = "0";

        draft.IsValid.ShouldBeTrue();
        draft.Applied().Settings.KeepAliveInterval.ShouldBe(0);
    }

    [Fact]
    public void AConnectTimeoutOfZeroIsNot()
    {
        // Nothing connects in no time, so zero here is a typo rather than a choice.
        var draft = HostDraft.New(new InventoryTree(), null, 0);
        draft.Name = "web-01";
        draft.Hostname = "web-01.example.com";
        draft.Settings.ConnectTimeout = "0";

        draft.IsValid.ShouldBeFalse();
        draft.ProblemSummary.ShouldContain("positive");
    }

    [Fact]
    public void TheFormSaysWhatABlankFieldWouldInherit()
    {
        var folder = new Folder
        {
            Name = "Production",
            Settings = new ConnectionSettings { Username = "ops", Port = 2222 },
        };
        var host = Host(parent: folder.Id);
        var tree = new InventoryTree([folder], [host]);

        var draft = HostDraft.For(tree, host);

        draft.UsernameHint.ShouldBe("ops — from Production");
        draft.PortHint.ShouldBe("2222 — from Production");
        draft.Settings.UsernameWatermark.ShouldBe(draft.UsernameHint);
        draft.InheritanceNote.ShouldContain("Production");
    }

    [Fact]
    public void MovingAHostChangesWhatItWouldInherit()
    {
        var production = new Folder { Name = "Production", Settings = new ConnectionSettings { Username = "ops" } };
        var staging = new Folder { Name = "Staging", Settings = new ConnectionSettings { Username = "deploy" } };
        var host = Host(parent: production.Id);
        var tree = new InventoryTree([production, staging], [host]);

        var draft = HostDraft.For(tree, host);
        draft.UsernameHint.ShouldBe("ops — from Production");

        draft.Parent = draft.Folders.Single(choice => choice.Id == staging.Id);

        draft.UsernameHint.ShouldBe("deploy — from Staging");
        draft.Settings.UsernameWatermark.ShouldBe("deploy — from Staging");
        draft.Applied().ParentId.ShouldBe(staging.Id);
    }

    [Fact]
    public void TheNearestFolderWinsTheInheritance()
    {
        var outer = new Folder { Name = "Production", Settings = new ConnectionSettings { Username = "ops" } };
        var inner = new Folder
        {
            Name = "Databases",
            ParentId = outer.Id,
            Settings = new ConnectionSettings { Username = "postgres" },
        };
        var host = Host(parent: inner.Id);
        var tree = new InventoryTree([outer, inner], [host]);

        HostDraft.For(tree, host).UsernameHint.ShouldBe("postgres — from Databases");
    }

    [Fact]
    public void AHostWithNoFolderInheritsNothingAndSaysSo()
    {
        var host = Host();
        var tree = new InventoryTree(connections: [host]);
        var draft = HostDraft.For(tree, host);

        draft.UsernameHint.ShouldContain(Environment.UserName);
        draft.PortHint.ShouldBe("22 — the default");
        draft.InheritanceNote.ShouldContain("Nothing above this");
    }

    [Fact]
    public void AFolderCannotBeMovedInsideItself()
    {
        var outer = new Folder { Name = "Production" };
        var inner = new Folder { Name = "Databases", ParentId = outer.Id };
        var tree = new InventoryTree([outer, inner]);

        var draft = FolderDraft.For(tree, outer);

        // Its own subtree is not even offered as a destination.
        draft.Folders.Select(choice => choice.Id).ShouldNotContain(outer.Id);
        draft.Folders.Select(choice => choice.Id).ShouldNotContain(inner.Id);
    }

    [Fact]
    public void AFolderMovedInsideItselfAnywayIsRefused()
    {
        // The picker cannot offer it, but a draft built in code can still try.
        var outer = new Folder { Name = "Production" };
        var inner = new Folder { Name = "Databases", ParentId = outer.Id };
        var tree = new InventoryTree([outer, inner]);

        var draft = FolderDraft.For(tree, outer);
        draft.Parent = new FolderChoice("Databases", inner.Id);

        draft.IsValid.ShouldBeFalse();
        draft.ProblemSummary.ShouldContain("inside itself");
    }

    [Fact]
    public void AFolderNeedsAName()
    {
        var draft = FolderDraft.New(new InventoryTree(), null, 0);

        draft.IsValid.ShouldBeFalse();

        draft.Name = "Production";

        draft.IsValid.ShouldBeTrue();
        draft.Applied().Name.ShouldBe("Production");
    }

    [Fact]
    public void AFolderKeepsItsSettingsAndItsPlaceWhenRenamed()
    {
        var folder = new Folder
        {
            Name = "Producton",
            SortIndex = 3,
            Settings = new ConnectionSettings { Username = "ops", ForwardAgent = true },
        };
        var tree = new InventoryTree([folder]);

        var draft = FolderDraft.For(tree, folder);
        draft.Name = "Production";
        var saved = draft.Applied();

        saved.Id.ShouldBe(folder.Id);
        saved.SortIndex.ShouldBe(3);
        saved.Settings.Username.ShouldBe("ops");
        saved.Settings.ForwardAgent.ShouldBe(true);
    }

    [Fact]
    public void AFolderPickerOffersTheTopLevelFirst()
    {
        var outer = new Folder { Name = "Production" };
        var inner = new Folder { Name = "Databases", ParentId = outer.Id };
        var tree = new InventoryTree([outer, inner]);

        var choices = FolderChoice.Of(tree);

        choices[0].ShouldBe(FolderChoice.Root);
        choices[1].Label.ShouldBe("Production");
        // Indented, so the shape of the tree survives being flattened into a list.
        choices[2].Label.ShouldBe("    Databases");
    }

    [Fact]
    public void ANewHostStartsEmptyAndAtTheEnd()
    {
        var folder = new Folder { Name = "Production" };
        var tree = new InventoryTree([folder], [Host(parent: folder.Id) with { SortIndex = 4 }]);

        var draft = HostDraft.New(tree, folder.Id, 5);

        draft.IsNew.ShouldBeTrue();
        draft.Title.ShouldBe("New host");
        draft.Name.ShouldBe("");
        draft.Parent.Id.ShouldBe(folder.Id);
        draft.Applied().SortIndex.ShouldBe(5);
    }

    [Fact]
    public void AHostCanBePointedAtASharedCredential()
    {
        var credential = new Credential { Name = "Production key", Method = CredentialMethod.IdentityFile };
        var host = Host();
        var tree = new InventoryTree(connections: [host], credentials: [credential]);

        var draft = HostDraft.For(tree, host);
        draft.Settings.CredentialChoices.Select(choice => choice.Label).ShouldBe(["Inherited", "Production key"]);

        draft.Settings.CredentialChoice = draft.Settings.CredentialChoices[1];

        draft.Applied().Settings.CredentialId.ShouldBe(credential.Id);
    }

    [Fact]
    public void ACredentialTheLibraryHasLostIsKeptRatherThanCleared()
    {
        // The id is in the file. A form that drops it because it cannot name it
        // is a form that loses data on Save — and the host would silently start
        // authenticating some other way.
        var missing = NodeId.New();
        var host = Host(settings: new ConnectionSettings { CredentialId = missing });
        var tree = new InventoryTree(connections: [host]);

        var draft = HostDraft.For(tree, host);

        draft.Settings.CredentialChoice.Label.ShouldBe("A credential that is no longer here");
        draft.Applied().Settings.CredentialId.ShouldBe(missing);
    }

    [Fact]
    public void ChoosingInheritedClearsIt()
    {
        var credential = new Credential { Name = "Production key" };
        var host = Host(settings: new ConnectionSettings { CredentialId = credential.Id });
        var tree = new InventoryTree(connections: [host], credentials: [credential]);

        var draft = HostDraft.For(tree, host);
        draft.Settings.CredentialChoice = draft.Settings.CredentialChoices[0];

        draft.Applied().Settings.CredentialId.ShouldBeNull();
    }

    [Fact]
    public void AnEditedHostSaysWhichOne()
    {
        var host = Host();
        HostDraft.For(new InventoryTree(connections: [host]), host).Title.ShouldBe("Edit web-01");
    }
}
