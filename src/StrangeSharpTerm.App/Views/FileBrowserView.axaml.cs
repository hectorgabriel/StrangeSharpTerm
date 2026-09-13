using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>The file browser pane. See FileBrowserView.axaml.</summary>
public partial class FileBrowserView : UserControl
{
    public FileBrowserView() => AvaloniaXamlLoader.Load(this);

    public FileBrowserView(FileBrowserViewModel model) : this() => DataContext = model;

    /// <summary>
    /// Enter opens what is selected, Backspace goes up.
    ///
    /// The same two keys every file manager uses, and the only way to move
    /// around without a pointer — which also makes the pane something the
    /// headless driver can drive.
    /// </summary>
    private void Keyed(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel model)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                model.OpenCommand.Execute(model.Selected);
                e.Handled = true;
                break;
            case Key.Back:
                model.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// A double click opens a directory.
    ///
    /// On the row rather than the list, so the answer to "which one" is the row
    /// that was clicked rather than whatever happens to be selected.
    /// </summary>
    private void Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FileRow row } && DataContext is FileBrowserViewModel model)
            model.OpenCommand.Execute(row);
    }
}
