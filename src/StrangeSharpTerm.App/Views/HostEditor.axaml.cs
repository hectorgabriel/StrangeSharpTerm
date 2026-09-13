using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// The host editor. Closes with true when the draft was saved, false when it was
/// not — the dialog service turns that into the answer its caller asked for.
/// </summary>
public partial class HostEditor : Window
{
    public HostEditor()
    {
        InitializeComponent();
    }

    public HostEditor(HostDraft draft) : this()
    {
        DataContext = draft;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The name is the first thing anyone types, and a new host has nothing
        // else worth looking at. After layout: a control cannot take focus
        // before it has a place on screen to take it into.
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("NameField")?.Focus());
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);

    private void Save(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HostDraft draft)
            return;

        if (!draft.IsValid)
        {
            // Shown rather than spoken: the banner names every problem at once,
            // so fixing one does not reveal the next.
            this.FindControl<Border>("Problems")!.IsVisible = true;
            return;
        }

        Close(true);
    }
}
