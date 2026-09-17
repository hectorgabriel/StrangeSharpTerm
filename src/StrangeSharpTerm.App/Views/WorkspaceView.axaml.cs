using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>The workspace pane. See WorkspaceView.axaml.</summary>
public partial class WorkspaceView : UserControl
{
    private readonly TranslateTransform _gutterOffset = new();

    public WorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);

        if (this.FindControl<TextBlock>("Gutter") is { } gutter)
            gutter.RenderTransform = _gutterOffset;

        // The numbers have to move with the text or they are worse than no
        // numbers at all. The text box scrolls itself, so what is followed is
        // its own scroll viewer -- found once the template is applied, because
        // before that there is nothing inside the control to find.
        if (this.FindControl<TextBox>("Editor") is { } editor)
            editor.TemplateApplied += FollowTheEditor;
    }

    public WorkspaceView(HostWorkspaceViewModel model) : this() => DataContext = model;

    private void FollowTheEditor(object? sender, TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is not { } scroller)
            return;

        // A gutter that stayed put while the file scrolled would be a column of
        // numbers that lie. If the template ever stops having this part, the
        // numbers simply do not move and the editor still works.
        scroller.GetObservable(ScrollViewer.OffsetProperty).Subscribe(
            new AnonymousObserver<Vector>(offset => _gutterOffset.Y = -offset.Y));
    }

    /// <summary>Enter opens the folder that has been typed, as an address bar does.</summary>
    private void RootKeyed(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not HostWorkspaceViewModel model)
            return;
        model.OpenRootCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Enter names it, Escape gives up. Naming happens in the pane, so the keys do too.</summary>
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
    /// tree has everywhere, and the only way to work this pane without a
    /// pointer, which is also what lets the headless driver drive it.
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

    /// <summary>
    /// An observer built from a lambda.
    ///
    /// Avalonia's observables are plain <c>IObservable</c>, and the one thing
    /// wanted here is a callback; taking a reactive dependency to express that
    /// would be a package for four lines.
    /// </summary>
    private sealed class AnonymousObserver<T>(Action<T> next) : IObserver<T>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) => next(value);
    }
}
