using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <inheritdoc cref="HostEditor"/>
public partial class SnippetEditor : Window
{
    public SnippetEditor()
    {
        InitializeComponent();
    }

    public SnippetEditor(SnippetDraft draft) : this()
    {
        DataContext = draft;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("NameField")?.Focus());
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);

    private void Save(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnippetDraft draft)
            return;

        if (!draft.IsValid)
        {
            this.FindControl<Border>("Problems")!.IsVisible = true;
            return;
        }

        Close(true);
    }
}
