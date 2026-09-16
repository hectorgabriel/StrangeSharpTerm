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
    }

    /// <summary>
    /// Return runs it; Shift or Alt with it starts a new line.
    ///
    /// The same arrangement as the assistant pane, and new here: the pane that
    /// reaches every selected host at once was, until now, the one you could
    /// only start by clicking.
    ///
    /// In plan mode Return writes the plan rather than running it, because that
    /// is what the button under the caret says: nothing is run until a person
    /// has read the phases and pressed it again.
    /// </summary>
    private void Typed(object? sender, KeyEventArgs e)
    {
        if (_model is null || e.Key != Key.Enter)
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            return;

        e.Handled = true;
        if (_model.RunCommand.CanExecute(null))
            _model.RunCommand.Execute(null);
    }

    public void Dispose() => _model?.Dispose();
}
