using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// A host's tunnels. See TunnelsView.axaml.
///
/// Disposable, because the pane holds running forwards: closing it has to let
/// the ports go, or nothing will bind them again until the app quits. The model
/// is held in a field rather than read back off DataContext — that is a styled
/// property with thread affinity, and whoever closes a pane is not guaranteed to
/// be the UI thread.
/// </summary>
public partial class TunnelsView : UserControl, IDisposable
{
    private readonly TunnelsViewModel? _model;

    public TunnelsView() => AvaloniaXamlLoader.Load(this);

    public TunnelsView(TunnelsViewModel model) : this()
    {
        _model = model;
        DataContext = model;
    }

    public void Dispose() => _model?.Dispose();
}
