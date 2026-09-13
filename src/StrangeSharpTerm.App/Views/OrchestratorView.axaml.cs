using Avalonia.Controls;
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

    public void Dispose() => _model?.Dispose();
}
