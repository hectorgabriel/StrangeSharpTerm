using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace StrangeSharpTerm.App.Views;

/// <summary>
/// Asking the user something, as a question with an answer.
///
/// The Swift app kept eight sheet flags and a matching optional on its god
/// object, each with a hand-written binding, and could not present a sheet from
/// a sheet. A dialog here is a call that returns what the user chose, so the
/// nesting is ordinary and the flags are gone.
/// </summary>
public interface IDialogService
{
    /// <summary>Asks a question whose answer costs something. False when dismissed.</summary>
    Task<bool> Confirm(string title, string detail, string confirmLabel);
}

/// <summary>Answers without asking. For tests, and for a headless run.</summary>
public sealed class ScriptedDialogService(bool answer = false) : IDialogService
{
    public List<(string Title, string Detail)> Asked { get; } = [];

    public Task<bool> Confirm(string title, string detail, string confirmLabel)
    {
        Asked.Add((title, detail));
        return Task.FromResult(answer);
    }
}

public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    public async Task<bool> Confirm(string title, string detail, string confirmLabel)
    {
        var answered = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirm = new Button { Content = confirmLabel, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        confirm.Click += (_, _) => { answered.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { answered.TrySetResult(false); dialog.Close(); };
        // Dismissing the window is a "no": the safe answer when the question was
        // about losing something.
        dialog.Closed += (_, _) => answered.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            MaxWidth = 420,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = detail, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm },
                },
            },
        };

        if (owner() is { } parent)
            await dialog.ShowDialog(parent);
        else
            dialog.Show();
        return await answered.Task;
    }
}
