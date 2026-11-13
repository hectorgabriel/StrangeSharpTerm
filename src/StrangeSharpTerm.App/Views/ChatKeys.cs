using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// What the keys do in a chat field, once for both panes.
///
/// Return sends; Shift or Alt with it starts a new line. Up and Down walk
/// through what has been sent, and only from the first and last line: inside
/// a question they are how you move around it, and taking them would make a
/// long one uneditable. Escape stops whatever is running.
///
/// Heard on the way <em>down</em> to the field, not on the way back up. A
/// field that accepts returns handles Return itself, by starting a new line,
/// and marks it handled -- so a handler waiting for the key to come back up
/// is never told about it. That is what this used to be, and Return did
/// nothing but add a line.
/// </summary>
internal static class ChatKeys
{
    /// <summary>The class a field carries to be one of these.</summary>
    public const string Class = "chat";

    public const string Hint =
        "Return sends · Shift+Return starts a new line · ↑ and ↓ walk through what you sent · Esc stops";

    /// <param name="pane">The view the fields are in; the handler sits here and hears every one of them.</param>
    /// <param name="send">Sends what is typed, if anything can be sent now.</param>
    /// <param name="recall">Shows the entry before (1) or after (-1); false at either end.</param>
    /// <param name="stop">Stops what is running; false when nothing is.</param>
    public static void Attach(Control pane, Action send, Func<int, bool> recall, Func<bool> stop) =>
        pane.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => Typed(e, send, recall, stop),
            RoutingStrategies.Tunnel);

    private static void Typed(KeyEventArgs e, Action send, Func<int, bool> recall, Func<bool> stop)
    {
        if (e.Source is not TextBox field || !field.Classes.Contains(Class))
            return;

        switch (e.Key)
        {
            case Key.Enter:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                    return;

                // Handled even when nothing can be sent: a Return that added a
                // line because a turn was still running would be a surprise.
                e.Handled = true;
                send();
                return;

            case Key.Escape:
                e.Handled = stop();
                return;

            case Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.None && AtTheEdge(field, e.Key):
                if (recall(e.Key == Key.Up ? 1 : -1))
                {
                    e.Handled = true;
                    // The caret goes to the end of what was recalled, which is
                    // where you would want to carry on from.
                    Dispatcher.UIThread.Post(() => field.CaretIndex = field.Text?.Length ?? 0);
                }
                return;
        }
    }

    /// <summary>
    /// Whether the caret is at the edge the arrow would leave the field by: the
    /// first line going up, the last going down.
    /// </summary>
    internal static bool AtTheEdge(TextBox field, Key key)
    {
        var text = field.Text ?? "";
        var caret = Math.Clamp(field.CaretIndex, 0, text.Length);
        return key == Key.Up
            ? caret == 0 || text.LastIndexOf('\n', caret - 1) < 0
            : text.IndexOf('\n', caret) < 0;
    }
}
