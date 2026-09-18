using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// A host's folder as a pane: the tree and the editor side by side.
///
/// Both halves are controls of their own, because this machine's files use them
/// apart. See WorkspaceView.axaml.
/// </summary>
public partial class WorkspaceView : UserControl
{
    public WorkspaceView() => AvaloniaXamlLoader.Load(this);

    public WorkspaceView(HostWorkspaceViewModel model) : this() => DataContext = model;
}
