using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using StrangeSharpTerm.App.Theming;
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
public sealed class TerminalPaneView : UserControl, IThemedPane, IDisposable
{
    private readonly TerminalControl _terminal = new();
    private readonly TerminalRegistry? _registry;
    private TerminalPalette _palette;
    private bool _attached;

    public TerminalPaneView(TerminalSession session, TerminalPalette? palette = null, TerminalRegistry? registry = null)
    {
        Session = session;
        _registry = registry;
        _palette = palette ?? TerminalPalette.StrangeTermDark;

        // The control has already written the keystroke to the stream. This only
        // tells the registry, so broadcast can mirror it to the other panes.
        _terminal.InputSent += (_, text) => session.ObserveInput(text);

        // Keystrokes go where the focus is, and opening a pane by clicking a host
        // in the sidebar leaves the focus on that button. Clicking anywhere in
        // the pane hands it to the terminal, as clicking a terminal does
        // everywhere else.
        AddHandler(PointerPressedEvent, (_, _) => FocusTerminal(), RoutingStrategies.Tunnel);
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
        // Held as well as applied: a pane recoloured before it is loaded would
        // otherwise be repainted in its old colours when it finally attaches.
        _palette = palette;
        _terminal.Background = new SolidColorBrush(Opaque(palette.Background));
        _terminal.Foreground = new SolidColorBrush(Opaque(palette.Foreground));
        _terminal.CursorColor = Opaque(palette.Cursor);

        if (_terminal.Terminal?.Colors is not { } colours)
            return;
        colours.SetBackground((int)palette.Background);
        colours.SetForeground((int)palette.Foreground);
        colours.SetCursor((int)palette.Cursor);

        if (palette.Ansi is not { } ansi)
            return;
        for (var index = 0; index < ansi.Count; index++)
            colours.SetColor(index, (int)ansi[index]);
    }

    /// <summary>
    /// The channel is attached once the control is on screen, not in the
    /// constructor.
    ///
    /// The control builds its engine and its surface as it is loaded; handing it
    /// a connection first asks it to draw into something that does not exist
    /// yet. The spike connected after the window had opened for the same reason,
    /// and doing it in the constructor is what a first attempt gets wrong.
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_attached)
            return;
        _attached = true;

        // On loaded rather than on attached: the control applies its template and
        // builds its surface between the two, and a connection handed over before
        // that has nothing to draw into. The spike connected after its window had
        // opened, which hid the distinction.
        _terminal.AttachConnection(new SessionPtyConnection(Session));
        Apply(_palette);

        // After layout: a control cannot take focus before it has a place on
        // screen to take it into.
        Dispatcher.UIThread.Post(FocusTerminal, DispatcherPriority.Input);
    }

    /// <summary>
    /// Puts the keyboard on the thing that reads it.
    ///
    /// TerminalControl is a templated wrapper; the key and text handlers live on
    /// the TerminalView inside its template. Focusing the wrapper looks right and
    /// does nothing, which is why typing went nowhere: in the spike the control
    /// was the window's only content and took focus by default, and in a real
    /// window it never does.
    /// </summary>
    public void FocusTerminal()
    {
        Control target = _terminal.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault() is { } view
            ? view
            : _terminal;
        target.Focusable = true;
        target.Focus(NavigationMethod.Pointer);
    }

    /// <summary>
    /// Ends the pane: the engine goes, and the broadcast group forgets it.
    ///
    /// Not on detach, which is where this lived and was wrong. Splitting a tab
    /// moves the existing pane into a new layout, and a view that disposes itself
    /// on the way out cannot be moved: the first pane went dead the moment a
    /// second one opened beside it — still drawn, still holding an ssh session,
    /// and deaf to every keystroke. Whoever closes a pane says so.
    /// </summary>
    public void Dispose()
    {
        _registry?.Forget(Session.Id);
        _terminal.Dispose();
    }

    private static Color Opaque(uint colour)
    {
        var (red, green, blue) = TerminalPalette.Rgb(colour);
        return Color.FromRgb(red, green, blue);
    }
}
