using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>Filling a snippet's placeholders in before it runs. See SnippetRunDialog.axaml.</summary>
public partial class SnippetRunDialog : Window
{
    public SnippetRunDialog()
    {
        InitializeComponent();
    }

    public SnippetRunDialog(SnippetRunViewModel model) : this()
    {
        DataContext = model;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The first placeholder, so the dialog can be answered without reaching
        // for the mouse. Posted because the fields are built by a template.
        Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus());
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);

    private void Run(object? sender, RoutedEventArgs e)
    {
        // Enter reaches here through IsDefault even when a placeholder is empty,
        // and an empty one would send a literal {{name}} to the shell.
        if (DataContext is SnippetRunViewModel { IsComplete: true })
            Close(true);
    }
}
