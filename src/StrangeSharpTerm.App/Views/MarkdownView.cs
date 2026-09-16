using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// A whole answer: its paragraphs, its headings, its lists and its fenced
/// blocks, each drawn as what it is.
///
/// One control rather than a template per pane, because the same answer is
/// shown in two places -- beside one host, and collated across several -- and
/// the collated one was being dumped into a single TextBlock, backticks and
/// asterisks and all.
///
/// Built in code for the reason <see cref="PaneSplitView"/> is: what is in an
/// answer is not known until it arrives, and a list of blocks whose shapes
/// differ is not something a DataTemplate expresses without three nested
/// ItemsControls.
/// </summary>
public sealed class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Text));

    /// <summary>
    /// What the button on a shell block does, where there is one.
    ///
    /// Null in the collated answer, which is the point: its commands apply to
    /// hosts, plural, and picking one terminal would be picking the wrong one.
    /// </summary>
    public static readonly StyledProperty<ICommand?> StageCommandProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(StageCommand));

    private readonly StackPanel _blocks = new() { Spacing = 6 };

    static MarkdownView()
    {
        TextProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
        StageCommandProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public MarkdownView() => Child = _blocks;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? StageCommand
    {
        get => GetValue(StageCommandProperty);
        set => SetValue(StageCommandProperty, value);
    }

    private void Rebuild()
    {
        _blocks.Children.Clear();

        foreach (var block in Fences.Parse(Text))
        {
            switch (block)
            {
                case AnswerBlock.Prose prose:
                    foreach (var line in prose.Lines)
                        _blocks.Children.Add(Line(line));
                    break;

                case AnswerBlock.Code code:
                    _blocks.Children.Add(Card(code));
                    break;
            }
        }
    }

    /// <summary>One line of prose: a heading, a list item, or a paragraph.</summary>
    private static Control Line(ProseLine line)
    {
        var text = new MarkdownText
        {
            Spans = line.Spans,
            TextWrapping = TextWrapping.Wrap,
            CodeBrush = Brush("AccentBrush"),
        };

        if (line.Heading > 0)
        {
            // One weight for every level rather than six sizes. This pane is
            // 380 points wide; the difference between an h2 and an h3 there is
            // noise, and what a heading has to do is separate what is above it
            // from what is below.
            text.FontWeight = FontWeight.SemiBold;
            text.Margin = new Thickness(0, 8, 0, 0);
            return text;
        }

        if (line.Marker is not { Length: > 0 } marker)
            return text;

        // The marker in a column of its own, so a bullet that wraps lines up
        // under its own text rather than under the dot.
        var item = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            Margin = new Thickness(line.Depth * 14, 0, 0, 0),
        };

        var bullet = new TextBlock
        {
            Text = marker,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brush("MutedBrush"),
            MinWidth = marker == "•" ? 0 : 18,
        };

        Grid.SetColumn(bullet, 0);
        Grid.SetColumn(text, 1);
        item.Children.Add(bullet);
        item.Children.Add(text);
        return item;
    }

    /// <summary>
    /// A fenced block: what it is, what it says, and what can be done with it.
    ///
    /// Copy on every block, because a path or a config line is worth as much
    /// somewhere else as a command is. Type it only on a block the model tagged
    /// as shell, which is the rule that was already here: a model that did not
    /// say what it was writing has not earned a button that puts it on a server.
    /// </summary>
    private Control Card(AnswerBlock.Code code)
    {
        var header = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
        };

        var language = new TextBlock
        {
            Text = code.Language ?? "text",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("MutedBrush"),
        };
        Grid.SetColumn(language, 0);
        header.Children.Add(language);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(Action("Copy", () => Copy(code.Staged)));
        if (code.IsShell && StageCommand is { } stage)
            actions.Children.Add(Action("Type it", () => stage.Execute(code)));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var body = new SelectableTextBlock
        {
            Text = code.Text,
            FontFamily = new FontFamily("monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        return new Border
        {
            Classes = { "chip" },
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel { Children = { header, body } },
        };
    }

    private static Button Action(string label, Action run) => new()
    {
        Classes = { "row" },
        Padding = new Thickness(8, 2),
        Content = new TextBlock { Text = label, FontSize = 11 },
        Command = new Command(run),
    };

    /// <summary>
    /// Puts a block on the clipboard.
    ///
    /// Fire and forget: the write is asynchronous and there is nothing useful
    /// to do about a clipboard that refuses, which happens when another
    /// application is holding it and is not this app's business to report.
    /// </summary>
    private void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        _ = clipboard.SetDataAsync(transfer);
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.FindResource(key) as IBrush;

    /// <summary>A button's action, where there is no view model to put it on.</summary>
    private sealed class Command(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();
    }
}
