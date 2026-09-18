using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

/// <summary>The editor half of a workspace. See WorkspaceEditorView.axaml.</summary>
public partial class WorkspaceEditorView : UserControl
{
    private readonly TranslateTransform _gutterOffset = new();

    public WorkspaceEditorView()
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

    public WorkspaceEditorView(HostWorkspaceViewModel model) : this() => DataContext = model;

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
