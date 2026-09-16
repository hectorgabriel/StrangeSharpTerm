using StrangeSharpTerm.Terminal;

namespace StrangeSharpTerm.App.Theming;

/// <summary>
/// A theme, as everything outside a terminal needs it: surfaces, text, an
/// accent, and the three colours that mean something.
///
/// <see cref="TerminalPalette"/> is the other half — the background, foreground,
/// cursor and sixteen ANSI colours a terminal needs — and the two are joined by
/// <see cref="Id"/>. They stay apart because the terminal's half is used by
/// <c>stctl</c> and the headless driver, neither of which has a window.
///
/// Every value here is either measured from the Swift app's own screenshots or
/// derived from one that was; <c>build/theme/sample.py</c> re-measures them and
/// <c>docs/adr/0004-theme-colours.md</c> says which is which. Colours are
/// 24-bit RGB, as <see cref="TerminalPalette"/> keeps them.
/// </summary>
/// <param name="Rail">The deepest surface: the icon rail down the left edge.</param>
/// <param name="Sidebar">Behind the host list.</param>
/// <param name="Background">The content area, and what a terminal sits on.</param>
/// <param name="Surface">A card, a chip, a field: something raised off the background.</param>
/// <param name="Border">A hairline or a progress track — the line between two surfaces.</param>
/// <param name="Selection">Behind the selected row: the accent, laid over the sidebar.</param>
/// <param name="Text">What ordinary text is.</param>
/// <param name="Muted">
/// A label, a hostname, a caption: present, and not the point.
///
/// Present enough to read, which it was not. Both themes took this from
/// Dracula's comment colour, and a comment colour is chosen to recede behind
/// code -- against a chip it reached 2.51:1, under even the 3:1 floor for large
/// text. Almost everything wearing it in the assistant panes is content rather
/// than decoration: command output, the reason a command stopped at the gate,
/// what each host reported.
///
/// Each theme's is now its measured colour with every channel scaled by one
/// factor, so the hue is exactly what it was and only the brightness moved,
/// scaled until it clears 4.5:1 against that theme's surface -- the worst
/// background it appears on. See docs/adr/0004.
/// </param>
/// <param name="OnAccent">Text drawn on the accent, which is dark because the accent is not.</param>
public sealed record AppPalette(
    string Id,
    string Name,
    string Description,
    uint Rail,
    uint Sidebar,
    uint Background,
    uint Surface,
    uint Border,
    uint Selection,
    uint Text,
    uint Muted,
    uint Accent,
    uint OnAccent,
    uint Success,
    uint Warning,
    uint Danger)
{
    /// <summary>
    /// The original scheme. Its three surfaces and its accent are measured from
    /// its swatch in the Swift settings sheet; its text is the terminal
    /// foreground already ported; the rest is derived. See the ADR.
    /// </summary>
    public static AppPalette StrangeTermDark { get; } = new(
        "strangeterm-dark", "StrangeTerm Dark", "Near-black surfaces, teal accent",
        Rail: 0x111216,
        Sidebar: 0x16181D,
        Background: 0x1A1D23,
        Surface: 0x21252E,
        Border: 0x2A2E35,
        Selection: 0x19302E,
        Text: 0xE7EAF0,
        Muted: 0x7D8CA9,
        Accent: 0x2ED3A0,
        OnAccent: 0x111216,
        Success: Green,
        Warning: Orange,
        Danger: Red);

    /// <summary>
    /// Dracula. Every colour here is measured: the reference screenshots are all
    /// in this theme.
    /// </summary>
    public static AppPalette Dracula { get; } = new(
        "dracula", "Dracula", "The VS Code theme, purple accent",
        Rail: 0x191A21,
        Sidebar: 0x21222C,
        Background: 0x282A36,
        Surface: 0x343746,
        Border: 0x424450,
        Selection: 0x353147,
        Text: 0xF8F8F2,
        Muted: 0x889EE3,
        Accent: 0xBD93F9,
        OnAccent: 0x191A21,
        Success: Green,
        Warning: Orange,
        Danger: Red);

    /// <summary>
    /// The three colours that mean something rather than decorate. They do not
    /// change with the theme: red is danger in both, and the Swift app's own
    /// green, orange and red are the ones measured out of its screenshots — a
    /// host badge is its colour at 16% over the sidebar, which is how they were
    /// read back. Only surfaces, text and the accent are a theme's own.
    /// </summary>
    private const uint Green = 0x50FA7B;

    private const uint Orange = 0xFFB86C;

    private const uint Red = 0xFF5555;

    /// <summary>In the order the settings sheet lists them, original first.</summary>
    public static IReadOnlyList<AppPalette> BuiltIn { get; } = [StrangeTermDark, Dracula];

    /// <summary>The terminal half of the same theme, joined by id.</summary>
    public TerminalPalette Terminal => TerminalPalette.ById(Id);

    public static AppPalette ById(string? id) =>
        BuiltIn.FirstOrDefault(palette => palette.Id == id) ?? StrangeTermDark;

    /// <summary>By theme name, as a connection's <c>terminalTheme</c> setting records it.</summary>
    public static AppPalette ByName(string? name) =>
        BuiltIn.FirstOrDefault(palette => palette.Name == name) ?? StrangeTermDark;

    /// <summary>
    /// One colour laid over another. A tint is how this theme says "this row is
    /// selected" or "this host is red" without a second palette of pale colours.
    /// </summary>
    public static uint Blend(uint over, uint under, double alpha)
    {
        var (r1, g1, b1) = TerminalPalette.Rgb(over);
        var (r2, g2, b2) = TerminalPalette.Rgb(under);
        return (Mix(r1, r2) << 16) | (Mix(g1, g2) << 8) | Mix(b1, b2);

        uint Mix(byte a, byte b) => (uint)Math.Round(alpha * a + (1 - alpha) * b);
    }
}
