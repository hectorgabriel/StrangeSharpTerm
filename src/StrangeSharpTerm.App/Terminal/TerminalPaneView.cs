using Avalonia.Controls;
using Avalonia.Media;
using Iciclecreek.Terminal;
using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Terminal;

/// <summary>
/// One terminal on screen.
///
/// The renderer draws the bytes and the session reads them: both are attached to
/// the same stream, so they cannot disagree about what the terminal shows. Each
/// parses for its own purpose — the control to draw cells, the session to answer
/// "what is on screen" for the assistant and the headless driver — which costs a
/// second pass over the bytes and buys one honest answer instead of two.
/// </summary>
public sealed class TerminalPaneView : UserControl
{
    private readonly TerminalControl _terminal = new();
    private readonly TerminalRegistry? _registry;

    public TerminalPaneView(TerminalSession session, TerminalPalette? palette = null, TerminalRegistry? registry = null)
    {
        Session = session;
        _registry = registry;

        _terminal.AttachConnection(new SessionPtyConnection(session));
        Apply(palette ?? TerminalPalette.StrangeTermDark);

        // The control has already written the keystroke to the stream. This only
        // tells the registry, so broadcast can mirror it to the other panes.
        _terminal.InputSent += (_, text) => session.ObserveInput(text);
        // The title comes from the session's engine rather than the control's, so
        // a tab, the assistant and a headless dump all read the same one.
        session.TitleChanged += (_, title) => TitleChanged?.Invoke(this, title);
        session.Ended += (_, _) => Ended?.Invoke(this, EventArgs.Empty);

        registry?.Register(session);
        Content = _terminal;
    }

    public TerminalSession Session { get; }

    /// <summary>The title the far end set, for a tab to show.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>The shell exited or the connection dropped.</summary>
    public event EventHandler? Ended;

    /// <summary>
    /// Applies a theme's terminal colours, engine included: the brushes paint the
    /// control, and the palette decides what the sixteen ANSI codes mean.
    /// </summary>
    public void Apply(TerminalPalette palette)
    {
        _terminal.Background = new SolidColorBrush(Opaque(palette.Background));
        _terminal.Foreground = new SolidColorBrush(Opaque(palette.Foreground));
        _terminal.CursorColor = Opaque(palette.Cursor);

        var colours = _terminal.Terminal.Colors;
        colours.SetBackground((int)palette.Background);
        colours.SetForeground((int)palette.Foreground);
        colours.SetCursor((int)palette.Cursor);

        if (palette.Ansi is not { } ansi)
            return;
        for (var index = 0; index < ansi.Count; index++)
            colours.SetColor(index, (int)ansi[index]);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _registry?.Forget(Session.Id);
        _terminal.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private static Color Opaque(uint colour)
    {
        var (red, green, blue) = TerminalPalette.Rgb(colour);
        return Color.FromRgb(red, green, blue);
    }
}
