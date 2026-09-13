using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The credential library and its editor, drawn.
///
/// What only a window can show: that the fields the method does not want are
/// gone rather than merely ignored, that the password box is masked, and that
/// Save refuses a form that cannot work instead of closing on it.
/// </summary>
[Collection("window")]
public class CredentialWindowTests
{
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    private static IEnumerable<string?> Text(Visual root) => In<TextBlock>(root).Select(block => block.Text);

    [Fact]
    public void TheEditorShowsOnlyWhatTheMethodNeeds()
    {
        Headless.Run(() =>
        {
            var draft = CredentialDraft.New(new InMemorySecretStore(), 0);
            var window = new CredentialEditor(draft);
            window.Show();
            Settle(window);

            // An agent credential: no key file, and no secret of ours at all.
            Text(window).ShouldContain("The agent holds the key. Nothing is stored here.");
            In<TextBox>(window).Where(box => box.IsEffectivelyVisible).Count().ShouldBe(2);

            draft.Method = CredentialMethod.IdentityFile;
            Settle(window);

            // Key file and passphrase have appeared, and the label says which.
            In<TextBox>(window).Where(box => box.IsEffectivelyVisible).Count().ShouldBe(4);
            Text(window).ShouldContain("Passphrase");

            draft.Method = CredentialMethod.Password;
            Settle(window);

            In<TextBox>(window).Where(box => box.IsEffectivelyVisible).Count().ShouldBe(3);
            Text(window).ShouldContain("Password");

            window.Close();
        });
    }

    [Fact]
    public void TheSecretIsMaskedAndStartsEmptyEvenWhenOneIsStored()
    {
        Headless.Run(() =>
        {
            var credential = new Credential { Name = "Shared account", Method = CredentialMethod.Password };
            var secrets = new InMemorySecretStore();
            secrets.SetSecret(credential.SecretAccount, "hunter2");

            var window = new CredentialEditor(CredentialDraft.For(credential, secrets));
            window.Show();
            Settle(window);

            var masked = In<TextBox>(window).Single(box => box.PasswordChar != '\0');
            masked.Text.ShouldBeNullOrEmpty();

            // Nothing drawn anywhere says the password.
            Text(window).ShouldNotContain("hunter2");
            In<TextBox>(window).Select(box => box.Text).ShouldNotContain("hunter2");
            Text(window).ShouldContain("One is stored. Type to replace it, or leave this empty to keep it.");

            window.Close();
        });
    }

    [Fact]
    public void SaveRefusesAFormThatCannotWorkAndSaysWhy()
    {
        Headless.Run(() =>
        {
            var draft = CredentialDraft.New(new InMemorySecretStore(), 0);
            draft.Method = CredentialMethod.IdentityFile;
            var window = new CredentialEditor(draft);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            Settle(window);

            var save = In<Button>(window).Single(button => (button.Content as string) == "Save");
            var problems = window.FindControl<Border>("Problems")!;
            problems.IsVisible.ShouldBeFalse();

            Press(save);
            Settle(window);

            closed.ShouldBeFalse();
            problems.IsVisible.ShouldBeTrue();
            Text(problems).ShouldContain("A credential needs a name. A key credential needs the path to the key.");

            draft.Name = "Production key";
            draft.IdentityFile = "~/.ssh/id_ed25519";
            Press(save);
            Settle(window);

            closed.ShouldBeTrue();
        });
    }

    [Fact]
    public void TheLibraryListsEachCredentialWithWhatDependsOnIt()
    {
        Headless.Run(() =>
        {
            var key = new Credential { Name = "Production key", Method = CredentialMethod.IdentityFile, IdentityFile = "~/.ssh/id_ed25519" };
            var password = new Credential { Name = "Shared account", Method = CredentialMethod.Password, SortIndex = 1 };
            var tree = new InventoryTree(
                connections:
                [
                    new Connection
                    {
                        Name = "web-01",
                        Hostname = "web-01.example.com",
                        Settings = new ConnectionSettings { CredentialId = key.Id },
                    },
                ],
                credentials: [key, password]);
            var window = new CredentialsWindow(
                new CredentialsViewModel(
                    new InventoryViewModel(null, tree),
                    new InMemorySecretStore(),
                    new ScriptedDialogService()));
            window.Show();
            Settle(window);

            var shown = Text(window).ToArray();
            shown.ShouldContain("Production key");
            shown.ShouldContain("Key file");
            shown.ShouldContain("~/.ssh/id_ed25519");
            shown.ShouldContain("used by 1 host or folder");
            shown.ShouldContain("Shared account");
            shown.ShouldContain("used by nothing");

            window.Close();
        });
    }

    [Fact]
    public void EditAndDeleteAreDisabledUntilSomethingIsSelected()
    {
        Headless.Run(() =>
        {
            var credential = new Credential { Name = "Production key" };
            var model = new CredentialsViewModel(
                new InventoryViewModel(null, new InventoryTree(credentials: [credential])),
                new InMemorySecretStore(),
                new ScriptedDialogService());
            var window = new CredentialsWindow(model);
            window.Show();
            Settle(window);

            // Each button's content is a TextBlock rather than a string, so the
            // label is one level in.
            Button Named(string label) =>
                In<Button>(window).Single(button => (button.Content as TextBlock)?.Text == label);

            Named("Edit…").IsEffectivelyEnabled.ShouldBeFalse();
            Named("Delete…").IsEffectivelyEnabled.ShouldBeFalse();
            Named("Add…").IsEffectivelyEnabled.ShouldBeTrue();

            model.Selected = model.Rows[0];
            Settle(window);

            Named("Edit…").IsEffectivelyEnabled.ShouldBeTrue();
            Named("Delete…").IsEffectivelyEnabled.ShouldBeTrue();

            window.Close();
        });
    }

    [Fact]
    public void AnEmptyLibrarySaysWhatOneIsFor()
    {
        Headless.Run(() =>
        {
            var window = new CredentialsWindow(
                new CredentialsViewModel(
                    new InventoryViewModel(null, new InventoryTree()),
                    new InMemorySecretStore(),
                    new ScriptedDialogService()));
            window.Show();
            Settle(window);

            Text(window).Any(text => text is { } line && line.StartsWith("No credentials yet.")).ShouldBeTrue();

            window.Close();
        });
    }

    private static void Press(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
