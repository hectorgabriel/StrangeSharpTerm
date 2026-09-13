using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>The settings sheet. See SettingsWindow.axaml.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel model) : this()
    {
        DataContext = model;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Done(object? sender, RoutedEventArgs e) => Close(true);
}
