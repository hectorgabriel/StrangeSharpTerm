using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
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

        ChatKeys.Attach(
            this,
            send: () =>
            {
                if (model.AskCommand.CanExecute(null))
                    model.AskCommand.Execute(null);
            },
            recall: model.Recall,
            stop: () =>
            {
                if (!model.IsAsking)
                    return false;
                model.StopCommand.Execute(null);
                return true;
            });
    }

    public void Dispose() => _model?.Dispose();
}
