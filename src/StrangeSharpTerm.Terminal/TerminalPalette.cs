namespace StrangeSharpTerm.Terminal;

/// <summary>
/// The colours a terminal needs, as 24-bit RGB.
///
/// The values are the Swift app's, unchanged: a theme that looked like Dracula
/// there has to look like Dracula here, or the port is visibly not the same
/// program. The rest of a theme — surfaces, borders, accents — is the app's
/// business and arrives with the shell in M4.
/// </summary>
/// <param name="Ansi">
/// The sixteen ANSI colours, normal then bright, or null to leave the engine's
/// own defaults alone — which is what the original theme has always done.
/// </param>
public sealed record TerminalPalette(
    string Id,
    string Name,
    uint Background,
    uint Foreground,
    uint Cursor,
    IReadOnlyList<uint>? Ansi = null)
{
    /// <summary>The original scheme: near-black surfaces with a teal accent.</summary>
    public static TerminalPalette StrangeTermDark { get; } = new(
        "strangeterm-dark", "StrangeTerm Dark",
        Background: 0x1A1D23, Foreground: 0xE7EAF0, Cursor: 0x2ED3A0);

    /// <summary>Dracula, following the official palette the VS Code theme uses.</summary>
    public static TerminalPalette Dracula { get; } = new(
        "dracula", "Dracula",
        Background: 0x282A36, Foreground: 0xF8F8F2, Cursor: 0xF8F8F2,
        Ansi:
        [
            0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C,
            0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
            0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5,
            0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF,
        ]);

    public static IReadOnlyList<TerminalPalette> BuiltIn { get; } = [StrangeTermDark, Dracula];

    public static TerminalPalette ById(string? id) =>
        BuiltIn.FirstOrDefault(palette => palette.Id == id) ?? StrangeTermDark;

    /// <summary>By theme name, as a connection's <c>terminalTheme</c> setting records it.</summary>
    public static TerminalPalette ByName(string? name) =>
        BuiltIn.FirstOrDefault(palette => palette.Name == name) ?? StrangeTermDark;

    /// <summary>Split into red, green and blue, which is how every toolkit wants them.</summary>
    public static (byte Red, byte Green, byte Blue) Rgb(uint colour) =>
        ((byte)(colour >> 16), (byte)(colour >> 8), (byte)colour);
}
