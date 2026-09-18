using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>The tree half of a workspace. See WorkspaceTreeView.axaml.</summary>
public partial class WorkspaceTreeView : UserControl
{
    public WorkspaceTreeView() => AvaloniaXamlLoader.Load(this);

    public WorkspaceTreeView(HostWorkspaceViewModel model) : this() => DataContext = model;

    /// <summary>Enter opens the folder that has been typed, as an address bar does.</summary>
    private void RootKeyed(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not HostWorkspaceViewModel model)
            return;
        model.OpenRootCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Enter names it, Escape gives up. Naming happens in the tree, so the keys do too.</summary>
    private void NameKeyed(object? sender, KeyEventArgs e)
    {
        if (DataContext is not HostWorkspaceViewModel model)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                model.ConfirmNameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                model.CancelNameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Enter opens what is selected and Delete removes it — the two keys a file
    /// tree has everywhere, and the only way to work this without a pointer,
    /// which is also what lets the headless driver drive it.
    /// </summary>
    private void TreeKeyed(object? sender, KeyEventArgs e)
    {
        if (DataContext is not HostWorkspaceViewModel model)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                model.ActivateCommand.Execute(model.Selected);
                e.Handled = true;
                break;
            case Key.Delete or Key.Back:
                model.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// A double click opens a file, or folds a directory.
    ///
    /// On the row rather than the tree, so the answer to "which one" is the row
    /// that was clicked rather than whatever happens to be selected.
    /// </summary>
    private void Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorkspaceNode node } && DataContext is HostWorkspaceViewModel model)
            model.ActivateCommand.Execute(node);
    }
}
