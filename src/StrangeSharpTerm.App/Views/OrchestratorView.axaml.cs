using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// The orchestrator pane. See OrchestratorView.axaml.
///
/// Disposable for the same reason the assistant pane is: a run may be waiting on
/// an approval, and closing the pane has to answer it.
/// </summary>
public partial class OrchestratorView : UserControl, IDisposable
{
    private readonly OrchestratorViewModel? _model;

    public OrchestratorView() => AvaloniaXamlLoader.Load(this);

    public OrchestratorView(OrchestratorViewModel model) : this()
    {
        _model = model;
        DataContext = model;

        // As the assistant pane does it: the view model asks, the view has the window.
        model.Copied += (_, text) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                return;

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(text));
            _ = clipboard.SetDataAsync(transfer);
        };

        // In plan mode Return writes the plan rather than running it, because
        // that is what the button beside the field says: nothing is run until
        // a person has read the phases and pressed Run the plan.
        ChatKeys.Attach(
            this,
            send: () =>
            {
                if (model.RunCommand.CanExecute(null))
                    model.RunCommand.Execute(null);
            },
            recall: model.Recall,
            stop: () =>
            {
                if (!model.IsRunning)
                    return false;
                model.StopCommand.Execute(null);
                return true;
            });
    }

    public void Dispose() => _model?.Dispose();
}
