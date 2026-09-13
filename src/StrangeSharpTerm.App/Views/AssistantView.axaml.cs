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
    }

    /// <summary>
    /// Return asks. A single-line field, so there is nothing Return would
    /// otherwise do, and a question is short enough that a button alone would be
    /// a nuisance.
    /// </summary>
    private void Send(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _model is null)
            return;

        e.Handled = true;
        if (_model.AskCommand.CanExecute(null))
            _model.AskCommand.Execute(null);
    }

    public void Dispose() => _model?.Dispose();
}
