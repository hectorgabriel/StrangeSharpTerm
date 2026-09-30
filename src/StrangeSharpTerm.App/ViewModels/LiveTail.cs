namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// The end of a text that is still arriving, and only the end.
///
/// Spacing the frames (<see cref="Streamed"/>) made each redraw rarer but not
/// cheaper: every frame still re-wrapped everything the model had thought so
/// far, so the cost of a frame grew with the think and a long one kept a core
/// busy to the end. What a person reads while it streams is the last few lines
/// -- the rest has scrolled past -- so that is all that is laid out until it
/// stops. The whole of it is there to open once it has.
/// </summary>
internal static class LiveTail
{
    /// <summary>
    /// About a dozen lines of a pane's width. Enough to follow the argument,
    /// small enough that a frame costs the same at the ten-thousandth token as
    /// at the hundredth.
    /// </summary>
    internal const int Length = 1200;

    internal static string Of(string text)
    {
        if (text.Length <= Length)
            return text;

        // Cut at a word, so the first thing on screen is not half of one.
        var start = text.Length - Length;
        var space = text.IndexOfAny([' ', '\n'], start);
        if (space >= 0 && space - start < 80)
            start = space + 1;
        return "…" + text[start..];
    }
}
