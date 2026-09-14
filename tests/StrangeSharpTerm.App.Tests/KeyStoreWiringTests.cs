using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Mcp;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// Which store each kind of secret is actually read from.
///
/// Three kinds live in three services on purpose — a connection passphrase, a
/// provider API key and a tool server's token are revoked, rotated and lost
/// independently. The constants said so and were right; the wiring read one of
/// them from the wrong place, so a key saved in Settings was written to the
/// assistant's store and looked for in the connection store, and every pane
/// reported no API key. Nothing failed, because no test followed a key from
/// where it is written to where it is read.
/// </summary>
public class KeyStoreWiringTests
{
    private static Connection Host { get; } = new() { Name = "web-01", Hostname = "web-01.example.com" };

    /// <summary>A shell wired the way the real window wires one, with stores a test may touch.</summary>
    private static (ShellViewModel Shell, InMemorySecretStore AssistKeys) Window(AssistProviderId provider)
    {
        var assistKeys = new InMemorySecretStore();
        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [Host])),
            new FakeSessions(),
            (_, _) => new Avalonia.Controls.Border(),
            secrets: new InMemorySecretStore(),
            assist: new AssistSettings { Provider = provider },
            assistKeys: assistKeys);
        return (shell, assistKeys);
    }

    [Theory]
    [InlineData(AssistProviderId.DeepSeek)]
    [InlineData(AssistProviderId.Claude)]
    public void AKeySavedInSettingsIsTheKeyAPaneFinds(AssistProviderId provider)
    {
        var (shell, assistKeys) = Window(provider);

        // Exactly what the sheet does: write through the store it was handed.
        new SettingsViewModel(
            new AppTheme(),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            new AssistSettings { Provider = provider },
            assistKeys)
        {
            NewKey = "a-real-key",
        }.SaveKeyCommand.Execute(null);

        shell.Inventory.Selection = Host.Id;
        shell.OpenAssistantCommand.Execute(null);

        // The pane opened, which it cannot do without a backend.
        shell.Failure.ShouldBeNull();
        shell.Panes.ShouldHaveSingleItem();
    }

    [Fact]
    public void WithNothingSavedThePaneStillSaysSo()
    {
        var (shell, _) = Window(AssistProviderId.DeepSeek);

        shell.Inventory.Selection = Host.Id;
        shell.OpenAssistantCommand.Execute(null);

        shell.Failure.ShouldNotBeNull().ShouldContain("DeepSeek");
        shell.Panes.ShouldBeEmpty();
    }

    [Fact]
    public void AConnectionPassphraseIsNotAnApiKey()
    {
        // Writing one into the connection store must not make the assistant
        // think it has a key: they are different secrets in different services.
        var connectionSecrets = new InMemorySecretStore();
        connectionSecrets.SetSecret(AssistProvider.DeepSeek.KeyAccount, "not an api key");

        var shell = new ShellViewModel(
            new InventoryViewModel(null, new InventoryTree(connections: [Host])),
            new FakeSessions(),
            (_, _) => new Avalonia.Controls.Border(),
            secrets: connectionSecrets,
            assist: new AssistSettings { Provider = AssistProviderId.DeepSeek },
            assistKeys: new InMemorySecretStore());

        shell.Inventory.Selection = Host.Id;
        shell.OpenAssistantCommand.Execute(null);

        shell.Failure.ShouldNotBeNull().ShouldContain("DeepSeek");
    }

    [Fact]
    public void EachKindOfSecretHasAServiceOfItsOwn()
    {
        string[] services = [PlatformSecretStore.DefaultService, AssistKeys.Service, McpTokens.Service];

        services.Distinct().Count().ShouldBe(3);
    }
}
