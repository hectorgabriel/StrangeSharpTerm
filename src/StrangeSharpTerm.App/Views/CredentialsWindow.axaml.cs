using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>The credential library. See CredentialsWindow.axaml.</summary>
public partial class CredentialsWindow : Window
{
    public CredentialsWindow()
    {
        InitializeComponent();
    }

    public CredentialsWindow(CredentialsViewModel model) : this()
    {
        DataContext = model;
        model.Refresh();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Done(object? sender, RoutedEventArgs e) => Close(true);
}
