using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

public partial class ShellWindow : Window
{
    public ShellWindow() : this(new ShellViewModel(new InventoryViewModel()))
    {
    }

    public ShellWindow(ShellViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
