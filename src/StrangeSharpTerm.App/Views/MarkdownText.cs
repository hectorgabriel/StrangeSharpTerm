using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// A paragraph of an answer, with its bold, its italics and its inline code
/// drawn as what they are.
///
/// A SelectableTextBlock rather than a TextBlock, because an answer about a
/// server is full of things you need somewhere else: a path, a unit name, a
/// number to paste into a ticket. Nothing in these panes could be selected
/// before, which made the app a picture of an answer rather than an answer.
///
/// The spans are rebuilt rather than appended to, because an answer streams:
/// the text grows a few characters at a time and every one of them re-parses
/// the line it is on.
/// </summary>
public sealed class MarkdownText : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<InlineSpan>?> SpansProperty =
        AvaloniaProperty.Register<MarkdownText, IReadOnlyList<InlineSpan>?>(nameof(Spans));

    /// <summary>The brush for inline code, which wants to look like the terminal it is about.</summary>
    public static readonly StyledProperty<IBrush?> CodeBrushProperty =
        AvaloniaProperty.Register<MarkdownText, IBrush?>(nameof(CodeBrush));

    static MarkdownText()
    {
        SpansProperty.Changed.AddClassHandler<MarkdownText>((text, _) => text.Rebuild());
        CodeBrushProperty.Changed.AddClassHandler<MarkdownText>((text, _) => text.Rebuild());
    }

    public IReadOnlyList<InlineSpan>? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    public IBrush? CodeBrush
    {
        get => GetValue(CodeBrushProperty);
        set => SetValue(CodeBrushProperty, value);
    }

    private void Rebuild()
    {
        var inlines = Inlines;
        if (inlines is null)
            return;

        inlines.Clear();
        foreach (var span in Spans ?? [])
        {
            inlines.Add(span.Style switch
            {
                InlineStyle.Strong => new Run(span.Text) { FontWeight = FontWeight.SemiBold },
                InlineStyle.Emphasis => new Run(span.Text) { FontStyle = FontStyle.Italic },
                InlineStyle.Code => new Run(span.Text)
                {
                    FontFamily = new FontFamily("monospace"),
                    Foreground = CodeBrush ?? Foreground,
                },
                _ => new Run(span.Text),
            });
        }
    }
}
