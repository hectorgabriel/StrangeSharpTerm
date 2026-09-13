using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>A connected tool server. See McpServerEditor.axaml.</summary>
public partial class McpServerEditor : Window
{
    public McpServerEditor() => InitializeComponent();

    public McpServerEditor(McpServerDraft draft) : this() => DataContext = draft;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Save(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
