using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.Views;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// An answer drawn as what it is.
///
/// These need a window because what is asserted is the visual tree the control
/// builds: which runs are bold, which blocks got a button. The parsing itself
/// is <c>MarkdownTests</c> in the Assist suite, where it needs no window at all.
/// </summary>
[Collection("window")]
public class MarkdownViewTests
{
    private static Window Show(MarkdownView view)
    {
        var window = new Window { Content = view, Width = 380, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static IEnumerable<Run> Runs(Visual root) =>
        root.GetSelfAndVisualDescendants()
            .OfType<TextBlock>()
            .SelectMany(block => block.Inlines?.OfType<Run>() ?? []);

    [Fact]
    public void BoldAndInlineCodeAreDrawnAsThemselves()
    {
        Headless.Run(() =>
        {
            var view = new MarkdownView { Text = "The journal is **the whole of it**, per `journald.conf`." };
            var window = Show(view);

            var runs = Runs(window).ToArray();
            runs.ShouldContain(run => run.Text == "the whole of it" && run.FontWeight == FontWeight.SemiBold);
            runs.ShouldContain(run => run.Text == "journald.conf" && run.FontFamily.Name.Contains("mono"));

            // And the marks themselves are nowhere on screen.
            string.Concat(runs.Select(run => run.Text)).ShouldNotContain("**");
        });
    }

    /// <summary>
    /// Every block can be copied; only one the model tagged as shell gets the
    /// button that puts it on a server. The second half is the rule that was
    /// already here and must not have been loosened by giving it a toolbar.
    /// </summary>
    [Fact]
    public void EveryBlockCopiesAndOnlyAShellBlockIsTyped()
    {
        Headless.Run(() =>
        {
            var view = new MarkdownView
            {
                Text = "```sh\njournalctl --vacuum-size=1G\n```\n\n```json\n{\"a\": 1}\n```",
                StageCommand = new Noop(),
            };
            var window = Show(view);

            var labels = window.GetSelfAndVisualDescendants()
                .OfType<Button>()
                .SelectMany(button => (button.Content as TextBlock) is { } text ? new[] { text.Text } : [])
                .ToArray();

            labels.Count(label => label == "Copy").ShouldBe(2);
            labels.Count(label => label == "Type it").ShouldBe(1);
        });
    }

    /// <summary>
    /// With nowhere to type it -- the collated answer, whose commands apply to
    /// hosts plural -- the button is not offered at all, and Copy still is.
    /// </summary>
    [Fact]
    public void WithNoStageCommandNothingIsOfferedToBeTyped()
    {
        Headless.Run(() =>
        {
            var view = new MarkdownView { Text = "```sh\njournalctl --vacuum-size=1G\n```" };
            var window = Show(view);

            var labels = window.GetSelfAndVisualDescendants()
                .OfType<Button>()
                .SelectMany(button => (button.Content as TextBlock) is { } text ? new[] { text.Text } : [])
                .ToArray();

            labels.ShouldContain("Copy");
            labels.ShouldNotContain("Type it");
        });
    }

    /// <summary>An answer is worth nothing on screen if it cannot be taken off it.</summary>
    [Fact]
    public void TheAnswerCanBeSelected()
    {
        Headless.Run(() =>
        {
            var view = new MarkdownView { Text = "The journal is 37G.\n\n```sh\ndf -h /\n```" };
            var window = Show(view);

            var selectable = window.GetSelfAndVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            selectable.ShouldContain(block => block is MarkdownText);
            selectable.ShouldContain(block => block.Text == "df -h /");
        });
    }

    [Fact]
    public void ListsKeepTheirMarkersAndTheirText()
    {
        Headless.Run(() =>
        {
            var view = new MarkdownView { Text = "- first thing\n- second thing\n\n2. numbered" };
            var window = Show(view);

            var shown = window.GetSelfAndVisualDescendants()
                .OfType<TextBlock>()
                .Select(block => block.Text)
                .ToArray();

            shown.Count(text => text == "•").ShouldBe(2);
            shown.ShouldContain("2.");
            Runs(window).Select(run => run.Text).ShouldContain("first thing");
        });
    }

    private sealed class Noop : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) { }
    }
}
