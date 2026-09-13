using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>The snippet library. See SnippetsWindow.axaml.</summary>
public partial class SnippetsWindow : Window
{
    public SnippetsWindow()
    {
        InitializeComponent();
    }

    public SnippetsWindow(SnippetsViewModel model) : this()
    {
        DataContext = model;
        model.Refresh();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Done(object? sender, RoutedEventArgs e) => Close(true);
}
