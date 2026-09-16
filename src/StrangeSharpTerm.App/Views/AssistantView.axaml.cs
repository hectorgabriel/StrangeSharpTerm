using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// The assistant pane. See AssistantView.axaml.
///
/// Disposable, because the pane owns a loop that may be waiting on a person:
/// closing it has to answer that question, or a task waits forever on an
/// approval nobody will now see.
/// </summary>
public partial class AssistantView : UserControl, IDisposable
{
    private readonly AssistantViewModel? _model;

    public AssistantView() => AvaloniaXamlLoader.Load(this);

    public AssistantView(AssistantViewModel model) : this()
    {
        _model = model;
        DataContext = model;

        // The clipboard is reached through the window, which a view model has
        // no business holding, so it asks and this answers.
        model.Copied += (_, text) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                return;

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(text));
            _ = clipboard.SetDataAsync(transfer);
        };
    }

    /// <summary>
    /// Return asks; Shift or Alt with it starts a new line.
    ///
    /// The field takes returns now, so Return has something else it could mean
    /// and the modifier is what tells them apart. This is the arrangement every
    /// chat uses, and the one people try first.
    ///
    /// Up walks back through what has been asked here, and only from the first
    /// line: below that, Up is how you move around the question you are
    /// writing, and taking it would make a long question uneditable.
    /// </summary>
    private void Typed(object? sender, KeyEventArgs e)
    {
        if (_model is null || sender is not TextBox field)
            return;

        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                return;

            e.Handled = true;
            if (_model.AskCommand.CanExecute(null))
                _model.AskCommand.Execute(null);
            return;
        }

        if (e.Key is not (Key.Up or Key.Down) || !AtTheEdge(field, e.Key))
            return;

        if (_model.Recall(e.Key == Key.Up ? 1 : -1))
        {
            e.Handled = true;
            // The caret goes to the end of what was recalled, which is where
            // you would want to carry on from.
            Dispatcher.UIThread.Post(() => field.CaretIndex = field.Text?.Length ?? 0);
        }
    }

    /// <summary>
    /// Whether the caret is at the edge the arrow would leave the field by: the
    /// first line going up, the last going down.
    /// </summary>
    private static bool AtTheEdge(TextBox field, Key key)
    {
        var text = field.Text ?? "";
        var caret = Math.Clamp(field.CaretIndex, 0, text.Length);
        return key == Key.Up
            ? text.LastIndexOf('\n', Math.Max(0, caret - 1)) < 0
            : text.IndexOf('\n', caret) < 0;
    }

    public void Dispose() => _model?.Dispose();
}
