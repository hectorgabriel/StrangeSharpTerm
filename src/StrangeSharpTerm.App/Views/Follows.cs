using Avalonia;
using Avalonia.Controls;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// Keeps a ScrollViewer at the bottom while an answer streams into it, and
/// stops the moment the reader scrolls away.
///
/// A conversation that does not follow its own output makes you chase it with
/// the wheel; one that follows it unconditionally snatches the page back while
/// you are reading something further up. Every chat worth using does both, and
/// the rule is the same one: follow while the reader is already at the bottom.
///
/// Attached rather than written into each pane because two of them want it, and
/// a ScrollViewer is not something either view model should know about.
/// </summary>
public static class Follows
{
    /// <summary>
    /// How far from the bottom still counts as being at it.
    ///
    /// Not zero: a line arriving mid-scroll, or a fractional offset from a
    /// partly-visible row, would otherwise read as "the reader has moved" and
    /// stop the following for good.
    /// </summary>
    private const double Slack = 24;

    public static readonly AttachedProperty<bool> TheEndProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("TheEnd", typeof(Follows));

    static Follows() => TheEndProperty.Changed.AddClassHandler<ScrollViewer>(Attach);

    public static void SetTheEnd(ScrollViewer viewer, bool value) => viewer.SetValue(TheEndProperty, value);

    public static bool GetTheEnd(ScrollViewer viewer) => viewer.GetValue(TheEndProperty);

    private static void Attach(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.GetNewValue<bool>())
            viewer.ScrollChanged += Scrolled;
        else
            viewer.ScrollChanged -= Scrolled;
    }

    private static void Scrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
            return;

        // Content grew and the offset did not: something arrived. Whether to
        // follow it depends on where the reader was before it did, which is
        // this offset minus the growth.
        if (e.ExtentDelta.Y <= 0)
            return;

        var wasAt = viewer.Offset.Y;
        var wasBottom = viewer.Extent.Height - e.ExtentDelta.Y - viewer.Viewport.Height;
        if (wasAt < wasBottom - Slack)
            return;

        viewer.ScrollToEnd();
    }
}
