using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The snippet library, its editor, and the dialog that fills one in, drawn.
///
/// The last of those is the one worth a window test: it stands between a saved
/// command and a live server, and the preview it shows is the last thing anyone
/// sees before the line is sent.
/// </summary>
[Collection("window")]
public class SnippetWindowTests
{
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    /// <summary>What is actually drawn: a hidden block is still in the tree.</summary>
    private static IEnumerable<string?> Text(Visual root) =>
        In<TextBlock>(root).Where(block => block.IsEffectivelyVisible).Select(block => block.Text);

    private static Button Named(Visual root, string label) =>
        In<Button>(root).Single(button =>
            (button.Content as string) == label || (button.Content as TextBlock)?.Text == label);

    private static void Press(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void TheLibraryListsEachSnippetWithItsCommandAndScope()
    {
        Headless.Run(() =>
        {
            var folder = new Folder { Name = "Production" };
            var tree = new InventoryTree(
                [folder],
                snippets:
                [
                    new Snippet { Name = "Tail the log", Command = "tail -f /var/log/{{service}}.log" },
                    new Snippet { Name = "Deploy", Command = "deploy", FolderId = folder.Id, SortIndex = 1 },
                ]);
            var window = new SnippetsWindow(
                new SnippetsViewModel(new InventoryViewModel(null, tree), new ScriptedDialogService()));
            window.Show();
            Settle(window);

            var shown = Text(window).ToArray();
            shown.ShouldContain("Tail the log");
            shown.ShouldContain("tail -f /var/log/{{service}}.log");
            shown.ShouldContain("Everywhere · asks for service");
            shown.ShouldContain("In Production");

            window.Close();
        });
    }

    [Fact]
    public void EditAndDeleteAreDisabledUntilSomethingIsSelected()
    {
        Headless.Run(() =>
        {
            var model = new SnippetsViewModel(
                new InventoryViewModel(null, new InventoryTree(snippets: [new Snippet { Name = "uptime", Command = "uptime" }])),
                new ScriptedDialogService());
            var window = new SnippetsWindow(model);
            window.Show();
            Settle(window);

            Named(window, "Edit…").IsEffectivelyEnabled.ShouldBeFalse();
            Named(window, "Delete…").IsEffectivelyEnabled.ShouldBeFalse();

            model.Selected = model.Rows[0];
            Settle(window);

            Named(window, "Edit…").IsEffectivelyEnabled.ShouldBeTrue();
            Named(window, "Delete…").IsEffectivelyEnabled.ShouldBeTrue();

            window.Close();
        });
    }

    [Fact]
    public void TheEditorSaysWhatTheCommandWillAskFor()
    {
        Headless.Run(() =>
        {
            var draft = SnippetDraft.New(new InventoryTree(), 0);
            var window = new SnippetEditor(draft);
            window.Show();
            Settle(window);

            Text(window).ShouldContain("No placeholders. Write {{name}} in the command to be asked for a value.");

            draft.Command = "systemctl restart {{service}}";
            Settle(window);

            Text(window).ShouldContain("Asks for service before running.");

            window.Close();
        });
    }

    [Fact]
    public void SaveRefusesAnEmptyFormAndSaysWhy()
    {
        Headless.Run(() =>
        {
            var window = new SnippetEditor(SnippetDraft.New(new InventoryTree(), 0));
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            Settle(window);

            var problems = window.FindControl<Border>("Problems")!;
            problems.IsVisible.ShouldBeFalse();

            Press(Named(window, "Save"));
            Settle(window);

            closed.ShouldBeFalse();
            problems.IsVisible.ShouldBeTrue();
            Text(problems).ShouldContain("A snippet needs a name. A snippet needs a command.");

            window.Close();
        });
    }

    [Fact]
    public void TheCommandFieldTakesMoreThanOneLine()
    {
        // A saved command is often a pipeline, and a single-line field makes one
        // unreadable — so Enter must reach the field rather than the Save button.
        Headless.Run(() =>
        {
            var window = new SnippetEditor(SnippetDraft.New(new InventoryTree(), 0));
            window.Show();
            Settle(window);

            In<TextBox>(window).Any(box => box.AcceptsReturn).ShouldBeTrue();
            In<Button>(window).Any(button => button.IsDefault).ShouldBeFalse();

            window.Close();
        });
    }

    [Fact]
    public void TheFillingDialogPreviewsTheLineItWillSend()
    {
        Headless.Run(() =>
        {
            var snippet = new Snippet { Name = "Tail", Command = "tail -f /var/log/{{service}}.log" };
            var model = new SnippetRunViewModel(snippet, "web-01");
            var window = new SnippetRunDialog(model);
            window.Show();
            Settle(window);

            // Nothing filled in: the preview is still the template, and Run is out.
            Text(window).ShouldContain("tail -f /var/log/{{service}}.log");
            Named(window, "Run").IsEffectivelyEnabled.ShouldBeFalse();

            model.Values.Single().Value = "nginx";
            Settle(window);

            Text(window).ShouldContain("tail -f /var/log/nginx.log");
            Named(window, "Run").IsEffectivelyEnabled.ShouldBeTrue();

            window.Close();
        });
    }

    [Fact]
    public void ABroadcastIsSaidBeforeTheCommandRunsInEveryPane()
    {
        Headless.Run(() =>
        {
            var snippet = new Snippet { Name = "Uptime", Command = "uptime {{flag}}" };
            var window = new SnippetRunDialog(new SnippetRunViewModel(snippet, "web-01", paneCount: 4));
            window.Show();
            Settle(window);

            Text(window).ShouldContain("Broadcast is on: this runs in all 4 panes of this tab.");

            window.Close();
        });
    }

    [Fact]
    public void WithOnePaneNothingIsSaidAboutBroadcast()
    {
        Headless.Run(() =>
        {
            var snippet = new Snippet { Name = "Uptime", Command = "uptime {{flag}}" };
            var window = new SnippetRunDialog(new SnippetRunViewModel(snippet, "web-01"));
            window.Show();
            Settle(window);

            Text(window).Any(text => text?.StartsWith("Broadcast is on") == true).ShouldBeFalse();

            window.Close();
        });
    }
}
